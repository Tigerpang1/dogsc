"""Build a coverage-aware report for the controlled 30-minute QPC AV test."""
import csv
import hashlib
import json
import pathlib
import re
import statistics
import sys

import numpy as np


def read_json(path):
    return json.loads(pathlib.Path(path).read_text(encoding="utf-8-sig"))


def quantiles(values):
    values = np.asarray(values, dtype=np.float64)
    if not len(values):
        return {"count": 0, "median_ms": None, "p95_abs_ms": None, "p99_abs_ms": None, "max_abs_ms": None}
    absolute = np.abs(values)
    return {
        "count": int(len(values)),
        "median_ms": float(np.median(values)),
        "p95_abs_ms": float(np.percentile(absolute, 95)),
        "p99_abs_ms": float(np.percentile(absolute, 99)),
        "max_abs_ms": float(absolute.max()),
    }


def read_csv(path):
    with pathlib.Path(path).open("r", encoding="utf-8-sig", newline="") as stream:
        return list(csv.DictReader(stream))


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: summarize-long-sync-report.py <capture-qpc-long-final-v8 directory>")
    base = pathlib.Path(sys.argv[1]).resolve()
    sync = read_json(base / "qpc-av-30min-v2.sync-playback.json")
    analysis = read_json(base / "qpc-av-30min-v2.analysis.json")
    run = read_json(base / "qpc-av-30min.run.json")
    video = read_json(base / "qpc-av-30min-v2.ffprobe-video.json")
    audio = read_json(base / "qpc-av-30min-v2.ffprobe-audio.json")
    signal_diagnostics = read_json(base / "qpc-av-30min-v2.signal-diagnostics.json")
    fixture_csv = pathlib.Path(sync["Fixture"]).with_name("sync-export-fixture-analysis.csv")
    fixture_offsets = [float(row["audio_minus_video_ms"]) for row in read_csv(fixture_csv)]
    fixture_bias = float(statistics.median(fixture_offsets))
    matches = read_csv(base / "qpc-av-30min-v2.analysis.csv")
    match_rows = [
        {
            "video": float(row["video_flash_seconds"]),
            "audio": float(row["audio_tone_seconds"]),
            "raw": float(row["audio_minus_video_ms"]),
            "corrected": float(row["audio_minus_video_ms"]) - fixture_bias,
        }
        for row in matches
    ]
    expected_duration = 1800.0
    expected_cues = 1080
    expected_per_ten_minutes = 360
    frequency = float(sync["StopwatchFrequency"])
    audio_to_video_offset = (
        float(sync["AudioStartStopwatchTicks"]) - float(sync["FirstFrameObservedStopwatchTicks"])
    ) / frequency
    video_events = [row["video"] for row in match_rows] + [
        float(value) for value in analysis["unmatched_video_flash_seconds"]
    ]
    candidates = analysis["tone_candidate_spectrum"]
    classified_audio_video_times = [
        float(item["time_seconds"]) + audio_to_video_offset
        for item in candidates
        if item["accepted_as_1khz_fixture_tone"]
    ]
    wideband_audio_video_times = [float(item["time_seconds"]) + audio_to_video_offset for item in candidates]

    intervals = [("first_10min", 0.0, 600.0), ("middle_10min", 600.0, 1200.0), ("last_10min", 1200.0, 1800.1)]
    time_parts = []
    for name, low, high in intervals:
        part_matches = [row for row in match_rows if low <= row["video"] < high]
        part_raw = [row["raw"] for row in part_matches]
        part_corrected = [row["corrected"] for row in part_matches]
        flashes = sum(low <= value < high for value in video_events)
        tones = sum(low <= value < high for value in classified_audio_video_times)
        broad = sum(low <= value < high for value in wideband_audio_video_times)
        raw_stats = quantiles(part_raw)
        corrected_stats = quantiles(part_corrected)
        time_parts.append(
            {
                "part": name,
                "start_seconds": low,
                "end_seconds": high,
                "nominal_cues": expected_per_ten_minutes,
                "video_flashes": flashes,
                "classified_1khz_tones": tones,
                "wideband_audio_candidates": broad,
                "matched_cues": len(part_matches),
                "matched_nominal_coverage_percent": 100.0 * len(part_matches) / expected_per_ten_minutes,
                "raw_offsets": raw_stats,
                "fixture_bias_corrected_estimate": corrected_stats,
            }
        )

    raw_offsets = [row["raw"] for row in match_rows]
    corrected_offsets = [row["corrected"] for row in match_rows]
    analysis_stdout = (base / "qpc-av-30min.stdout.log").read_text(encoding="utf-8-sig")
    roi_guard_count = analysis_stdout.count("Verified controlled ROI at")
    roi_rows = read_csv(base / "qpc-av-30min-v2.roi-luma-sample.csv")
    roi_luma = [float(row["luma"]) for row in roi_rows]
    metrics = read_csv(base / "qpc-av-30min.metrics.csv")
    process_metrics = {}
    for name in ("FrameTrail.Capture.Probe", "ffmpeg", "ffplay"):
        rows = sorted((row for row in metrics if row["name"] == name), key=lambda row: float(row["elapsed_seconds"]))
        cpu_rates = []
        for before, after in zip(rows, rows[1:]):
            seconds = float(after["elapsed_seconds"]) - float(before["elapsed_seconds"])
            cpu_seconds = float(after["cpu_seconds"]) - float(before["cpu_seconds"])
            if seconds > 0 and cpu_seconds >= 0:
                cpu_rates.append(100.0 * cpu_seconds / seconds)
        process_metrics[name] = {
            "private_bytes_peak": max((int(row["private_bytes"]) for row in rows), default=None),
            "working_set_bytes_peak": max((int(row["working_set_bytes"]) for row in rows), default=None),
            "average_cpu_percent_from_10s_samples": statistics.mean(cpu_rates) if cpu_rates else None,
            "peak_cpu_percent_from_10s_samples": max(cpu_rates, default=None),
        }
    min_free_memory = min((int(row["available_memory_bytes"]) for row in metrics), default=None)
    min_free_disk = min((int(row["disk_free_bytes"]) for row in metrics), default=None)

    video_stream = video["streams"][0]
    audio_stream = audio["streams"][0]
    packet_warning = next((warning for warning in sync["AudioWarnings"] if warning.startswith("WASAPI packet timing:")), "")
    packet_fields = {name: int(value) for name, value in re.findall(r"(packets|timestampFallbacks|timestampErrors|discontinuities|devicePositionMismatches)=(\d+)", packet_warning)}
    packet_count = packet_fields.get("packets", 0)
    audio_duration = float(audio_stream["duration"])
    audio_frames = round(audio_duration * int(audio_stream["sample_rate"]))
    matched = len(match_rows)
    first_match = match_rows[0]["video"] if match_rows else None
    last_match = match_rows[-1]["video"] if match_rows else None
    matched_span = last_match - first_match if first_match is not None and last_match is not None else 0.0
    valid = (
        matched / expected_cues >= 0.95
        and matched / max(int(analysis["flash_events"]), int(analysis["tone_events"]), 1) >= 0.90
        and matched_span >= 1700.0
    )
    executable = pathlib.Path(run["Probe"])
    exe_sha = hashlib.sha256(executable.read_bytes()).hexdigest().upper()
    source_hashes = {}
    repo_root = base.parents[2]
    for relative in (
        "src/FrameTrail.Capture/FrameCaptureService.cs",
        "src/FrameTrail.Capture/FfmpegDesktopRecorder.cs",
        "src/FrameTrail.Capture/AudioPacketClock.cs",
        "src/FrameTrail.Capture/CaptureTrackService.cs",
        "src/FrameTrail.Capture/TimestampedWasapiCapture.cs",
    ):
        path = repo_root / relative
        source_hashes[relative] = hashlib.sha256(path.read_bytes()).hexdigest().upper()
    report = {
        "status": "passed" if valid else "invalid_sync_coverage",
        "component_results": {
            "video_capture_decode_and_duration": "passed" if int(video["streams"][0]["nb_read_frames"]) >= 108000 and abs(float(video["format"]["duration"]) - expected_duration) < 1.0 else "failed",
            "clocked_wav_duration": "passed" if abs(float(audio["streams"][0]["duration"]) - expected_duration) < 1.0 else "failed",
            "av_sync_signal_coverage": "passed" if valid else "failed",
        },
        "validity_gates": {
            "nominal_cue_match_coverage_minimum_percent": 95.0,
            "bidirectional_detected_event_coverage_minimum_percent": 90.0,
            "matched_event_span_minimum_seconds": 1700.0,
            "actual_pass": valid,
        },
        "reason": "Only the first ~994 seconds contain matched tone/flash cues; the WAV continues for the full capture, so this is not a valid 30-minute AV synchronization sample." if not valid else None,
        "run": {
            "started_utc": run["StartedUtc"],
            "finished_utc": run["FinishedUtc"],
            "elapsed_seconds": run["ElapsedSeconds"],
            "requested_capture_seconds": run["DurationRequestedSeconds"],
            "probe_exit_code": run["ExitCode"],
            "probe_executable": str(executable),
            "probe_executable_sha256": exe_sha,
            "capture_clock_source_file_sha256": source_hashes,
            "fixture": sync["Fixture"],
            "capture_rect_physical": sync["CaptureBounds"],
            "display_rect_physical": sync["DisplayBounds"],
            "encoder": sync["EncoderName"],
            "roi_player_hwnd": sync["PlayerWindowHandle"],
            "roi_guard_checks": roi_guard_count,
            "getpixel_samples": len(roi_rows),
            "getpixel_luma_min": min(roi_luma) if roi_luma else None,
            "getpixel_luma_max": max(roi_luma) if roi_luma else None,
            "getpixel_distinct_levels": sorted(set(roi_luma)),
            "available_memory_min_bytes": min_free_memory,
            "disk_free_min_bytes": min_free_disk,
            "process_metrics": process_metrics,
        },
        "video": {
            "path": str(base / "qpc-av-30min-v2.mp4"),
            "ffprobe_json": str(base / "qpc-av-30min-v2.ffprobe-video.json"),
            "codec": video_stream["codec_name"],
            "width": int(video_stream["width"]),
            "height": int(video_stream["height"]),
            "average_frame_rate": video_stream["avg_frame_rate"],
            "duration_seconds": float(video_stream["duration"]),
            "frames_decoded": int(video_stream["nb_read_frames"]),
            "bytes": int(video["format"]["size"]),
            "nominal_fixture_cues": expected_cues,
            "detected_flashes": int(analysis["flash_events"]),
            "missing_nominal_flashes": expected_cues - int(analysis["flash_events"]),
        },
        "audio": {
            "path": str(base / "qpc-av-30min-v2.wav"),
            "ffprobe_json": str(base / "qpc-av-30min-v2.ffprobe-audio.json"),
            "codec": audio_stream["codec_name"],
            "sample_rate_hz": int(audio_stream["sample_rate"]),
            "channels": int(audio_stream["channels"]),
            "duration_seconds": float(audio_stream["duration"]),
            "bytes": int(audio["format"]["size"]),
            "classified_1khz_tone_events": int(analysis["tone_events"]),
            "missing_nominal_tone_cues": expected_cues - int(analysis["tone_events"]),
            "wideband_candidates": int(analysis["wideband_tone_candidate_count"]),
            "rejected_non_fixture_candidates": int(analysis["rejected_non_fixture_tone_candidate_count"]),
            "warnings": sync["AudioWarnings"],
        },
        "sync": {
            "analysis_json": str(base / "qpc-av-30min-v2.analysis.json"),
            "analysis_csv": str(base / "qpc-av-30min-v2.analysis.csv"),
            "matched_cues": matched,
            "nominal_cue_match_coverage_percent": 100.0 * matched / expected_cues,
            "detected_event_coverage_percent": float(analysis["matched_coverage_percent_of_detected_events"]),
            "wideband_audio_candidate_coverage_percent": float(analysis["matched_coverage_percent_of_wideband_audio_candidates"]),
            "first_match_video_seconds": first_match,
            "last_match_video_seconds": last_match,
            "matched_span_seconds": matched_span,
            "raw_audio_minus_video": quantiles(raw_offsets),
            "matched_span_drift_ms_per_minute": analysis["drift_ms_per_minute"],
            "fixture_bias_estimate": {
                "analysis_csv": str(fixture_csv),
                "measured_cues": len(fixture_offsets),
                "median_audio_minus_video_ms": fixture_bias,
                "method": "Median of three measured fixture cue offsets; approximate bias removal, not per-cue calibration or proof of physical AV accuracy.",
            },
            "fixture_bias_corrected_estimate": {
                "correction_ms": -fixture_bias,
                **quantiles(corrected_offsets),
            },
            "first_middle_last_full_capture_10min_parts": time_parts,
            "tone_spectrum_rule": analysis["tone_spectrum_rule"],
            "audio_signal_diagnostics_json": str(base / "qpc-av-30min-v2.signal-diagnostics.json"),
            "audio_signal_diagnostics": signal_diagnostics["expected_flash_local_100ms_windows"],
            "candidate_burst_duration_ms": signal_diagnostics["wideband_candidate_duration_ms"],
            "raw_wav_minute_signal_stats_csv": signal_diagnostics["raw_wav_minute_signal_stats_csv"],
            "raw_wav_ten_second_signal_stats_csv": signal_diagnostics["raw_wav_ten_second_signal_stats_csv"],
            "silence_summary": signal_diagnostics["silence_summary"],
        },
        "clock": {
            "stopwatch_frequency": sync["StopwatchFrequency"],
            "first_frame_observed_stopwatch_ticks": sync["FirstFrameObservedStopwatchTicks"],
            "audio_start_stopwatch_ticks": sync["AudioStartStopwatchTicks"],
            "audio_start_after_video_first_frame_ms": 1000.0 * audio_to_video_offset,
            "first_frame_anchor": "FFmpeg ddagrab setpts print observation translated to Stopwatch using a launch-time wall-clock pair; estimate, not native frame PTS.",
            "packet_timing_warning": sync["AudioWarnings"][-1],
        },
        "signal_source_diagnosis": {
            "cause": "unresolved",
            "decoded_audio_sample_count": audio_frames,
            "aggregate_wasapi_packet_count": packet_count,
            "aggregate_packet_rate_per_second": packet_count / audio_duration if audio_duration else None,
            "average_sample_frames_per_packet_if_uniform": audio_frames / packet_count if packet_count else None,
            "timestamp_fallback_packets": packet_fields.get("timestampFallbacks"),
            "timestamp_error_packets": packet_fields.get("timestampErrors"),
            "data_discontinuity_packets": packet_fields.get("discontinuities"),
            "device_position_mismatches": packet_fields.get("devicePositionMismatches"),
            "per_packet_timestamps_or_flags_saved": False,
            "silent_packet_count_saved": False,
            "clock_filled_silence_frame_count_saved": False,
            "silence_summary": signal_diagnostics["silence_summary"],
            "inference": "The aggregate packet count corresponds to roughly 97 packets/sec over the complete WAV and is compatible with regular callbacks, including explicit silent buffers. Because no per-packet SILENT flags, timestamps, or clock-filled-silence counts were retained, this cannot distinguish silent loopback buffers from gaps that the timeline pump padded with zeros.",
            "ffplay_stderr_saved": False,
            "ffplay_stderr_note": "CaptureScenarios reads ffplay stderr after the looped run and then discards the string; the runner stderr file is the Probe stderr and is empty. No historical SDL/audio-device diagnostics are available.",
            "source_review": [
                "TimestampedWasapiCapture.ReadAvailablePackets copies SILENT-flagged buffers as zero bytes and invokes PacketAvailable after ReleaseBuffer.",
                "CaptureTrackService.PumpAudioTimelineAsync independently fills output frames with WriteAudioSilence when no due packet covers the active clock, so a full-duration WAV alone does not prove audible source continuity.",
                "CaptureTrackService summary retains aggregate packet/fallback/discontinuity/device-position counters but not a per-packet timeline, SILENT count, or synthetic-silence frame count.",
                "CaptureScenarios.RunSyncPlaybackAsync drains ffplay stderr into a local string after stopping but does not store it in the summary or a sidecar file.",
            ],
            "next_isolated_test_not_run": "Run a short controlled loopback probe with scheduled known 1 kHz playback and silence intervals; capture packet QPC/device position, flags (especially SILENT and DATA_DISCONTINUITY), packet frame counts, per-second packet/zero-fill counters, render endpoint ID changes, and persist ffplay stderr. No new playback was run during this diagnosis.",
        },
        "artifacts": {
            "run_json": str(base / "qpc-av-30min.run.json"),
            "stdout_log": str(base / "qpc-av-30min.stdout.log"),
            "stderr_log": str(base / "qpc-av-30min.stderr.log"),
            "metrics_csv": str(base / "qpc-av-30min.metrics.csv"),
            "parts_metrics_csv": str(base / "qpc-av-30min-v2.parts-metrics.csv"),
            "roi_luma_csv": str(base / "qpc-av-30min-v2.roi-luma-sample.csv"),
            "tone_rejection_selftest": "tests/FrameTrail.Capture.Probe/tone-spectrum-selftest.py",
            "tone_classifier_module": "tests/FrameTrail.Capture.Probe/tone_spectrum.py",
            "signal_diagnostic_script": "tests/FrameTrail.Capture.Probe/diagnose-sync-audio-signal.py",
            "report_generator_script": "tests/FrameTrail.Capture.Probe/summarize-long-sync-report.py",
        },
        "limitations": [
            "The nominal total is 1080 cues based on three cues per 5-second source fixture cycle. The video detector found 1065 flashes, while the 1 kHz classifier found 486 audio events and matched 485.",
            "Matched cues span only the first part of the recording. The time-based first/middle/last rows use the full 1800-second capture, so zero later matches remain visible.",
            "The WAV remains 1800 seconds long and packet QPC fallback/error counts are zero, but classified fixture-tone candidates cease near 994 seconds. The cause is unresolved; no full-length drift claim is made.",
            "Subtracting the measured fixture median bias of -35 ms is an estimate only; it is not per-cue calibration and does not prove absolute physical AV accuracy.",
            "The test verified one physical display and its output-index mapping only; multi-monitor output-index behavior is unverified.",
        ],
    }
    output = base / "qpc-av-30min-v2.report.json"
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"report": str(output), "status": report["status"], "matched_cues": matched,
                      "nominal_coverage_percent": report["sync"]["nominal_cue_match_coverage_percent"],
                      "matched_span_seconds": matched_span, "parts": time_parts}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
