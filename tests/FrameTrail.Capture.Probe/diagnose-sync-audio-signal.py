"""Inspect numeric WAV signal statistics without listening to or transcribing audio."""
import csv
import json
import pathlib
import subprocess
import sys

import numpy as np

from tone_spectrum import target_power_fraction


RATE = 48000
CHANNELS = 2
SAMPLES_PER_SECOND = RATE * CHANNELS
WINDOW_SAMPLES = RATE // 10
SEARCH_OFFSETS_SECONDS = np.arange(-0.25, 0.2501, 0.05)


def read_exact(stream, count):
    chunks = []
    remaining = count
    while remaining:
        chunk = stream.read(remaining)
        if not chunk:
            break
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def band_rms(samples, sample_rate=RATE):
    samples = np.asarray(samples, dtype=np.float64)
    samples -= samples.mean()
    spectrum = np.fft.rfft(samples * np.hanning(len(samples)))
    frequencies = np.fft.rfftfreq(len(samples), 1 / sample_rate)
    power = np.abs(spectrum) ** 2
    target = power[(frequencies >= 980) & (frequencies <= 1020)].sum()
    # Compensate for the Hann window's coherent gain and one-sided real FFT power.
    return float(np.sqrt(2.0 * target) * 2.0 / len(samples))


def minute_spectral_summary(mono):
    windows = np.lib.stride_tricks.sliding_window_view(mono, WINDOW_SAMPLES)[::WINDOW_SAMPLES]
    if not len(windows):
        return {
            "one_khz_target_power_fraction_median": 0.0,
            "one_khz_target_power_fraction_p95": 0.0,
            "one_khz_target_band_rms_median": 0.0,
            "one_khz_target_band_rms_p95": 0.0,
            "comparison_band_600_1400_rms_median": 0.0,
        }
    spectra = np.fft.rfft(windows * np.hanning(WINDOW_SAMPLES), axis=1)
    frequencies = np.fft.rfftfreq(WINDOW_SAMPLES, 1 / RATE)
    powers = np.abs(spectra) ** 2
    target = powers[:, (frequencies >= 980) & (frequencies <= 1020)].sum(axis=1)
    comparison = powers[:, (frequencies >= 600) & (frequencies <= 1400)].sum(axis=1)
    ratios = np.divide(target, comparison, out=np.zeros_like(target), where=comparison > 0)
    target_rms = np.sqrt(2.0 * target) * 2.0 / WINDOW_SAMPLES
    comparison_rms = np.sqrt(2.0 * comparison) * 2.0 / WINDOW_SAMPLES
    return {
        "one_khz_target_power_fraction_median": float(np.median(ratios)),
        "one_khz_target_power_fraction_p95": float(np.percentile(ratios, 95)),
        "one_khz_target_band_rms_median": float(np.median(target_rms)),
        "one_khz_target_band_rms_p95": float(np.percentile(target_rms, 95)),
        "comparison_band_600_1400_rms_median": float(np.median(comparison_rms)),
    }


def percentile(values, p):
    return float(np.percentile(np.asarray(values, dtype=np.float64), p)) if values else None


def main():
    if len(sys.argv) != 5:
        raise SystemExit("Usage: diagnose-sync-audio-signal.py <ffmpeg.exe> <wav> <sync-analysis.json> <output-dir>")
    ffmpeg, wav_arg, analysis_arg, output_arg = sys.argv[1:]
    wav = pathlib.Path(wav_arg)
    analysis = json.loads(pathlib.Path(analysis_arg).read_text(encoding="utf-8-sig"))
    sync = json.loads(pathlib.Path(analysis_arg.replace(".analysis.json", ".sync-playback.json")).read_text(encoding="utf-8-sig"))
    output_dir = pathlib.Path(output_arg)
    output_dir.mkdir(parents=True, exist_ok=True)
    fixture_csv = pathlib.Path(sync["Fixture"]).with_name("sync-export-fixture-analysis.csv")
    with fixture_csv.open(encoding="utf-8-sig", newline="") as stream:
        fixture_offsets = [float(row["audio_minus_video_ms"]) for row in csv.DictReader(stream)]
    fixture_bias_seconds = float(np.median(fixture_offsets)) / 1000.0
    audio_to_video_offset = (
        float(sync["AudioStartStopwatchTicks"]) - float(sync["FirstFrameObservedStopwatchTicks"])
    ) / float(sync["StopwatchFrequency"])
    with (output_dir / "qpc-av-30min-v2.analysis.csv").open(encoding="utf-8-sig", newline="") as stream:
        matched_flashes = [float(row["video_flash_seconds"]) for row in csv.DictReader(stream)]
    flash_times = sorted(matched_flashes + [float(value) for value in analysis["unmatched_video_flash_seconds"]])
    candidate_times = [
        (float(item["time_seconds"]), bool(item["accepted_as_1khz_fixture_tone"]), float(item["duration_ms"]))
        for item in analysis["tone_candidate_spectrum"]
    ]
    cmd = [ffmpeg, "-v", "error", "-i", str(wav), "-vn", "-acodec", "pcm_f32le", "-ar", str(RATE), "-ac", str(CHANNELS), "-f", "f32le", "pipe:1"]
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    minute_rows = []
    ten_second_rows = []
    cue_window_rows = []
    seconds_read = 0
    expected_audio_times = [
        (video_time, video_time - audio_to_video_offset + fixture_bias_seconds)
        for video_time in flash_times
    ]
    try:
        while True:
            raw = read_exact(proc.stdout, RATE * CHANNELS * 4 * 60)
            if not raw:
                break
            if len(raw) % (CHANNELS * 4):
                raise RuntimeError("FFmpeg returned a partial float frame")
            samples = np.frombuffer(raw, dtype="<f4").reshape((-1, CHANNELS))
            frames = len(samples)
            minute_start = seconds_read
            minute_end = seconds_read + frames / RATE
            rms = float(np.sqrt(np.mean(samples.astype(np.float64) ** 2)))
            peak = float(np.max(np.abs(samples))) if frames else 0.0
            zero_fraction = float(np.count_nonzero(samples == 0.0) / samples.size) if samples.size else 1.0
            minute_rows.append({
                "minute_start_seconds": minute_start,
                "duration_seconds": frames / RATE,
                "stereo_rms": rms,
                "peak_absolute": peak,
                "strict_zero_sample_fraction": zero_fraction,
            })
            ten_second_frames = RATE * 10
            for local_frame in range(0, frames, ten_second_frames):
                section = samples[local_frame:min(local_frame + ten_second_frames, frames)]
                if not len(section):
                    continue
                section_seconds = len(section) / RATE
                ten_second_rows.append({
                    "segment_start_seconds": minute_start + local_frame / RATE,
                    "duration_seconds": section_seconds,
                    "stereo_rms": float(np.sqrt(np.mean(section.astype(np.float64) ** 2))),
                    "peak_absolute": float(np.max(np.abs(section))),
                    "strict_zero_sample_fraction": float(np.count_nonzero(section == 0.0) / section.size),
                })
            mono = samples.astype(np.float64).mean(axis=1)
            minute_rows[-1].update(minute_spectral_summary(mono))
            for video_time, expected_audio_time in expected_audio_times:
                if minute_start - 0.30 <= expected_audio_time <= minute_end + 0.30:
                    best_fraction = 0.0
                    best_band_rms = 0.0
                    best_window_rms = 0.0
                    best_offset = None
                    for offset in SEARCH_OFFSETS_SECONDS:
                        start = int(round((expected_audio_time + offset - minute_start) * RATE))
                        if start < 0 or start + WINDOW_SAMPLES > len(mono):
                            continue
                        window = mono[start:start + WINDOW_SAMPLES].copy()
                        fraction = target_power_fraction(window, RATE)
                        current_band_rms = band_rms(window)
                        current_window_rms = float(np.sqrt(np.mean(window ** 2)))
                        if best_offset is None or fraction > best_fraction:
                            best_fraction = fraction
                            best_band_rms = current_band_rms
                            best_window_rms = current_window_rms
                            best_offset = float(offset)
                    if best_offset is not None:
                        cue_window_rows.append({
                            "video_flash_seconds": video_time,
                            "expected_audio_window_seconds": expected_audio_time,
                            "search_window_plus_minus_seconds": 0.25,
                            "sampled_offset_seconds_at_max_fraction": best_offset,
                            "max_target_power_fraction": best_fraction,
                            "target_band_rms": best_band_rms,
                            "mono_window_rms": best_window_rms,
                            "after_last_classified_tone": expected_audio_time > max(
                                (item["time_seconds"] for item in analysis["tone_candidate_spectrum"] if item["accepted_as_1khz_fixture_tone"]),
                                default=0.0,
                            ),
                        })
            seconds_read += frames / RATE
            if frames < RATE * 60:
                break
        stderr = proc.stderr.read().decode("utf-8", "replace")
        exit_code = proc.wait(timeout=30)
        if exit_code:
            raise RuntimeError(f"FFmpeg decode failed with {exit_code}: {stderr}")
    finally:
        if proc.poll() is None:
            proc.kill()
            proc.wait(timeout=5)

    # Some flashes land within 300 ms of a 60-second decode chunk boundary. Merge
    # overlapping searches so each flash contributes exactly one numeric row.
    unique_windows = {}
    for row in cue_window_rows:
        key = round(float(row["video_flash_seconds"]), 6)
        current = unique_windows.get(key)
        if current is None or row["max_target_power_fraction"] > current["max_target_power_fraction"]:
            unique_windows[key] = row
    cue_window_rows = list(unique_windows.values())
    cue_window_rows.sort(key=lambda row: row["video_flash_seconds"])

    # Mark candidate duration distribution to reveal long RMS bursts that can merge cues.
    durations = [item[2] for item in candidate_times]
    early_tone_band = [
        row["target_band_rms"] for row in cue_window_rows
        if row["expected_audio_window_seconds"] <= 994.0 and row["max_target_power_fraction"] >= 0.85
    ]
    late_rows = [row for row in cue_window_rows if row["expected_audio_window_seconds"] > 994.0]
    zero_segments = [row for row in ten_second_rows if row["peak_absolute"] == 0.0]
    report = {
        "method": "Numeric-only analysis of the owned WASAPI WAV; no listening, speech recognition, or transcription.",
        "wav": str(wav.resolve()),
        "duration_decoded_seconds": seconds_read,
        "format": {"sample_rate_hz": RATE, "channels": CHANNELS, "sample_format": "float32"},
        "fixture_bias_median_estimate_ms": 1000.0 * fixture_bias_seconds,
        "audio_start_minus_first_frame_observation_ms": 1000.0 * audio_to_video_offset,
        "raw_wav_minute_signal_stats_csv": str((output_dir / "qpc-av-30min-v2.minute-signal-stats.csv").resolve()),
        "raw_wav_ten_second_signal_stats_csv": str((output_dir / "qpc-av-30min-v2.ten-second-signal-stats.csv").resolve()),
        "expected_flash_windows_csv": str((output_dir / "qpc-av-30min-v2.expected-flash-audio-windows.csv").resolve()),
        "minute_signal_stats": minute_rows,
        "ten_second_signal_stats": ten_second_rows,
        "silence_summary": {
            "zero_peak_segment_count": len(zero_segments),
            "first_zero_peak_segment_start_seconds": zero_segments[0]["segment_start_seconds"] if zero_segments else None,
            "last_nonzero_peak_segment_end_seconds": max((row["segment_start_seconds"] + row["duration_seconds"] for row in ten_second_rows if row["peak_absolute"] > 0.0), default=None),
            "definition": "A zero-peak segment has every decoded float32 sample exactly equal to zero; nonzero can include unrelated system sound and does not establish fixture audio.",
        },
        "wideband_candidate_duration_ms": {
            "count": len(durations),
            "median": float(np.median(durations)) if durations else None,
            "p95": percentile(durations, 95),
            "max": max(durations) if durations else None,
            "over_250ms_count": sum(value > 250 for value in durations),
            "over_500ms_count": sum(value > 500 for value in durations),
        },
        "expected_flash_local_100ms_windows": {
            "rule": "For each detected video flash after the cue timestamp mapping, scan 100 ms mono windows every 50 ms over +/-250 ms; report the maximum 980-1020 Hz power fraction and absolute band/window RMS. The per-minute raw-WAV spectrum uses non-overlapping 100 ms mono windows without bandpass filtering.",
            "total_flash_windows_with_audio": len(cue_window_rows),
            "after_last_classified_tone_count": len(late_rows),
            "after_last_classified_tone_power_fraction_ge_0_85": sum(row["max_target_power_fraction"] >= 0.85 for row in late_rows),
            "after_last_classified_tone_median_max_target_fraction": float(np.median([row["max_target_power_fraction"] for row in late_rows])) if late_rows else None,
            "after_last_classified_tone_max_target_fraction": max((row["max_target_power_fraction"] for row in late_rows), default=None),
            "early_classified_tone_target_band_rms_median": float(np.median(early_tone_band)) if early_tone_band else None,
            "late_target_band_rms_median": float(np.median([row["target_band_rms"] for row in late_rows])) if late_rows else None,
            "late_mono_window_rms_median": float(np.median([row["mono_window_rms"] for row in late_rows])) if late_rows else None,
        },
        "wasapi_aggregate_warnings": sync["AudioWarnings"],
        "warning_timing_limit": "Aggregate packet discontinuity/device-position counters have no per-packet timestamps; this report cannot associate them with any cue or minute.",
    }
    with (output_dir / "qpc-av-30min-v2.minute-signal-stats.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(minute_rows[0]) if minute_rows else [])
        writer.writeheader()
        writer.writerows(minute_rows)
    with (output_dir / "qpc-av-30min-v2.ten-second-signal-stats.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(ten_second_rows[0]) if ten_second_rows else [])
        writer.writeheader()
        writer.writerows(ten_second_rows)
    with (output_dir / "qpc-av-30min-v2.expected-flash-audio-windows.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(cue_window_rows[0]) if cue_window_rows else [])
        writer.writeheader()
        writer.writerows(cue_window_rows)
    report_path = output_dir / "qpc-av-30min-v2.signal-diagnostics.json"
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"report": str(report_path), **report["expected_flash_local_100ms_windows"],
                      "candidate_duration": report["wideband_candidate_duration_ms"],
                      "silence_summary": report["silence_summary"],
                      "decoded_seconds": seconds_read}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
