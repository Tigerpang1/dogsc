"""Analyze fixture sync pulses with robust p99 candidate gating (v3); v2 remains unchanged."""
import csv
import json
import pathlib
import subprocess
import sys

import numpy as np
from tone_spectrum import (
    COMPARISON_BAND_HZ,
    ENVELOPE_SEARCH_STEP_MS,
    ENVELOPE_TILE_MS,
    ENVELOPE_SEARCH_STEP_MS,
    ENVELOPE_TILE_MS,
    MIN_TARGET_POWER_FRACTION,
    RULE_VERSION,
    SAMPLE_RATE_HZ,
    STABLE_ENVELOPE_POLICY_VERSION,
    STABLE_ENVELOPE_WINDOW_MS,
    TARGET_BAND_HZ,
)
from tone_detection_v3 import detect_audio_tones


def run(args):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout


def parse_video_timestamps_json(payload):
    """Read frame PTS fields from ffprobe JSON and ignore frame side-data objects."""
    probe = json.loads(payload.decode("utf-8"))
    frames = probe.get("frames")
    if not isinstance(frames, list):
        raise ValueError("ffprobe JSON has no frames array")
    timestamps = []
    for index, frame in enumerate(frames):
        value = frame.get("best_effort_timestamp_time")
        try:
            timestamp = float(value)
        except (TypeError, ValueError) as exc:
            raise ValueError(f"ffprobe frame {index} has no valid best_effort_timestamp_time") from exc
        if not np.isfinite(timestamp):
            raise ValueError(f"ffprobe frame {index} has a non-finite best_effort_timestamp_time")
        timestamps.append(timestamp)
    return np.asarray(timestamps, dtype=np.float64)


def main():
    if len(sys.argv) != 6:
        raise SystemExit("Usage: analyze-ffplay-sync-v3.py <ffmpeg.exe> <ffprobe.exe> <capture.mp4> <summary.json> <analysis.csv>")
    ffmpeg, ffprobe, media_arg, summary_arg, output_arg = sys.argv[1:]
    media, summary_path, output = pathlib.Path(media_arg), pathlib.Path(summary_arg), pathlib.Path(output_arg)
    summary = json.loads(summary_path.read_text(encoding="utf-8"))
    pts_raw = run([ffprobe, "-v", "error", "-select_streams", "v:0", "-show_frames",
                   "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", str(media)])
    video_times = parse_video_timestamps_json(pts_raw)
    pixels = 16 * 9
    video_raw = run([ffmpeg, "-v", "error", "-i", str(media), "-vf", "scale=16:9:flags=area,format=gray",
                     "-an", "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "gray", "-"])
    if len(video_raw) % pixels:
        raise RuntimeError("Unexpected decoded video frame byte count")
    levels = np.frombuffer(video_raw, dtype=np.uint8).reshape((-1, pixels)).mean(axis=1)
    if len(video_times) != len(levels):
        raise RuntimeError(f"Video PTS/frame count mismatch: {len(video_times)} vs {len(levels)}")
    baseline = float(np.median(levels))
    bright = np.flatnonzero(levels > baseline + 35)
    flashes = []
    for index in bright:
        if not flashes or index - flashes[-1][-1] > 3:
            flashes.append([int(index)])
        else:
            flashes[-1].append(int(index))
    flash_events = [float(video_times[group[0]]) for group in flashes if len(group) >= 2]

    audio_path = pathlib.Path(summary["AudioPath"])
    audio_raw = run([ffmpeg, "-v", "error", "-i", str(audio_path), "-vn", "-af",
                     "pan=mono|c0=0.5*c0+0.5*c1,bandpass=f=1000:width_type=h:width=180,aresample=8000",
                     "-f", "f32le", "-ac", "1", "-"])
    samples = np.frombuffer(audio_raw, dtype="<f4")
    spectral_raw = run([ffmpeg, "-v", "error", "-i", str(audio_path), "-vn", "-af",
                        "pan=mono|c0=0.5*c0+0.5*c1,aresample=8000",
                        "-f", "f32le", "-ac", "1", "-"])
    spectral_samples = np.frombuffer(spectral_raw, dtype="<f4")
    detection = detect_audio_tones(samples, spectral_samples, SAMPLE_RATE_HZ)
    rms = detection["rms"]
    gate_stats = detection["statistics"]
    tone_candidates = detection["candidates"]
    tone_events = detection["tone_events"]

    frequency = int(summary["StopwatchFrequency"])
    video_zero = int(summary["FirstFrameObservedStopwatchTicks"])
    audio_zero = int(summary["AudioStartStopwatchTicks"])
    # Match by absolute capture-clock time; the first bright startup frame can be unrelated to a cue.
    matches = []
    remaining_video = set(range(len(flash_events)))
    remaining_audio = set(range(len(tone_events)))
    capture_start_offset = (audio_zero - video_zero) / frequency
    for audio_index, audio_time in enumerate(tone_events):
        expected_video = audio_time + capture_start_offset
        if not remaining_video:
            break
        video_index = min(remaining_video, key=lambda i: abs(flash_events[i] - expected_video))
        video_time = flash_events[video_index]
        offset_ms = ((audio_zero - video_zero) / frequency + audio_time - video_time) * 1000
        if abs(offset_ms) <= 250:
            matches.append((video_time, audio_time, offset_ms))
            remaining_video.remove(video_index)
            remaining_audio.remove(audio_index)
    if matches:
        x = np.array([(a + b) / 2 for a, b, _ in matches], dtype=np.float64)
        y = np.array([c for _, _, c in matches], dtype=np.float64)
        slope_ms_per_min = float(np.polyfit(x, y, 1)[0] * 60)
        offsets = np.abs(y)
        span = max(float(x.max() - x.min()), 1e-9)
        thirds = []
        for label, low, high in (("first", 0.0, 1 / 3), ("middle", 1 / 3, 2 / 3), ("last", 2 / 3, 1.000001)):
            selected = [offset for position, offset in zip(x, y) if low <= (position - float(x.min())) / span < high]
            if selected:
                selected = np.asarray(selected, dtype=np.float64)
                thirds.append({"part": label, "count": int(len(selected)), "median_audio_minus_video_ms": float(np.median(selected)),
                               "p95_abs_audio_minus_video_ms": float(np.percentile(np.abs(selected), 95)),
                               "max_abs_audio_minus_video_ms": float(np.max(np.abs(selected)))})
        initial = np.asarray([offset for position, offset in zip(x, y) if position <= float(x.min()) + 20], dtype=np.float64)
    else:
        slope_ms_per_min = None
        offsets = np.array([], dtype=np.float64)
        thirds = []
        initial = np.array([], dtype=np.float64)
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("w", encoding="utf-8", newline="") as f:
        writer = csv.writer(f)
        writer.writerow(["video_flash_seconds", "audio_tone_seconds", "audio_minus_video_ms"])
        writer.writerows([[f"{v:.6f}", f"{a:.6f}", f"{o:.3f}"] for v, a, o in matches])
    report = {"video": str(media), "audio": str(audio_path), "video_frames": len(video_times),
              "tone_spectrum_rule": {"candidate_gate_policy": gate_stats.get("candidate_gate_policy"),
                                     "candidate_gate_rms_percentile": gate_stats.get("rms_percentile"),
                                     "candidate_gate_median_rms": gate_stats.get("median_rms"),
                                     "candidate_gate_p99_rms": gate_stats.get("p99_rms"),
                                     "candidate_gate_maximum_rms": gate_stats.get("maximum_rms"),
                                     "candidate_gate_actual_threshold_rms": gate_stats.get("actual_threshold_rms"),
                                     "candidate_gate_minimum_rms_rise": gate_stats.get("minimum_rms_rise"),
                                     "candidate_gate_threshold_fraction": gate_stats.get("threshold_fraction"),
                                     "candidate_gate_window_ms": gate_stats.get("window_ms"),
                                     "candidate_gate_hop_ms": gate_stats.get("hop_ms"),
                                     "version": RULE_VERSION, "sample_rate_hz": SAMPLE_RATE_HZ,
                                     "window_policy": STABLE_ENVELOPE_POLICY_VERSION,
                                     "window_duration_ms": STABLE_ENVELOPE_WINDOW_MS,
                                     "window_samples": round(SAMPLE_RATE_HZ * STABLE_ENVELOPE_WINDOW_MS / 1000),
                                     "envelope_tile_ms": ENVELOPE_TILE_MS,
                                     "envelope_search_step_ms": ENVELOPE_SEARCH_STEP_MS,
                                     "envelope_threshold_fraction": 0.5,
                                     "fft_bin_width_hz": SAMPLE_RATE_HZ / round(SAMPLE_RATE_HZ * STABLE_ENVELOPE_WINDOW_MS / 1000),
                                     "target_band_hz": list(TARGET_BAND_HZ),
                                     "comparison_band_hz": list(COMPARISON_BAND_HZ),
                                     "minimum_target_power_fraction": MIN_TARGET_POWER_FRACTION},
              "flash_events": len(flash_events), "tone_events": len(tone_events), "matched_events": len(matches),
              "wideband_tone_candidate_count": len(tone_candidates),
              "rejected_non_fixture_tone_candidate_count": sum(not item["accepted_as_1khz_fixture_tone"] for item in tone_candidates),
              "tone_candidate_spectrum": tone_candidates,
              "match_tolerance_ms": 250,
              "matched_coverage_percent_of_detected_events": 100.0 * len(matches) / max(len(flash_events), len(tone_events), 1),
              "matched_coverage_percent_of_video_events": 100.0 * len(matches) / max(len(flash_events), 1),
              "matched_coverage_percent_of_classified_tone_events": 100.0 * len(matches) / max(len(tone_events), 1),
              "matched_coverage_percent_of_wideband_audio_candidates": 100.0 * len(matches) / max(len(tone_candidates), 1),
              "unmatched_video_flash_seconds": [flash_events[i] for i in sorted(remaining_video)],
              "unmatched_audio_tone_seconds": [tone_events[i] for i in sorted(remaining_audio)],
              "rejected_non_fixture_tone_candidate_seconds": [item["time_seconds"] for item in tone_candidates if not item["accepted_as_1khz_fixture_tone"]],
              "median_audio_minus_video_ms": float(np.median([m[2] for m in matches])) if matches else None,
              "p95_abs_audio_minus_video_ms": float(np.percentile(np.abs([m[2] for m in matches]), 95)) if matches else None,
              "p99_abs_audio_minus_video_ms": float(np.percentile(offsets, 99)) if len(offsets) else None,
              "max_abs_audio_minus_video_ms": float(np.max(offsets)) if len(offsets) else None,
              "initial_20s": {"count": int(len(initial)), "p95_abs_audio_minus_video_ms": float(np.percentile(np.abs(initial), 95)) if len(initial) else None,
                              "max_abs_audio_minus_video_ms": float(np.max(np.abs(initial))) if len(initial) else None},
              "drift_ms_per_minute": slope_ms_per_min, "first_match": matches[0] if matches else None,
              "last_match": matches[-1] if matches else None, "first_middle_last": thirds, "analysis_csv": str(output)}
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
