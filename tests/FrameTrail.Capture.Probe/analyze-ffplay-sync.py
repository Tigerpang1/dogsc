"""Measure recurring fixture flashes and 1 kHz pulses in a screen/WASAPI capture pair."""
import csv
import json
import pathlib
import subprocess
import sys

import numpy as np


def run(args):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout


def main():
    if len(sys.argv) != 6:
        raise SystemExit("Usage: analyze-ffplay-sync.py <ffmpeg.exe> <ffprobe.exe> <capture.mp4> <summary.json> <analysis.csv>")
    ffmpeg, ffprobe, media_arg, summary_arg, output_arg = sys.argv[1:]
    media, summary_path, output = pathlib.Path(media_arg), pathlib.Path(summary_arg), pathlib.Path(output_arg)
    summary = json.loads(summary_path.read_text(encoding="utf-8"))
    pts_raw = run([ffprobe, "-v", "error", "-select_streams", "v:0", "-show_frames",
                   "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", str(media)])
    video_times = np.array([float(line) for line in pts_raw.decode("ascii", "ignore").splitlines() if line.strip()])
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
    window, hop, sample_rate = 160, 40, 8000
    windows = np.lib.stride_tricks.sliding_window_view(samples, window)[::hop]
    rms = np.sqrt(np.mean(windows.astype(np.float64) ** 2, axis=1))
    audio_times = np.arange(len(rms), dtype=np.float64) * hop / sample_rate
    audio_baseline = float(np.median(rms))
    audio_threshold = audio_baseline + max(0.00003, (float(rms.max()) - audio_baseline) * 0.15)
    active = np.flatnonzero(rms > audio_threshold)
    bursts = []
    for index in active:
        if not bursts or index - bursts[-1][-1] > 10:
            bursts.append([int(index)])
        else:
            bursts[-1].append(int(index))
    tone_events = [float(audio_times[group[0]]) for group in bursts if len(group) >= 8]

    frequency = int(summary["StopwatchFrequency"])
    video_zero = int(summary["FirstFrameObservedStopwatchTicks"])
    audio_zero = int(summary["AudioStartStopwatchTicks"])
    # Match by absolute capture-clock time; the first bright startup frame can be unrelated to a cue.
    matches = []
    remaining_video = set(range(len(flash_events)))
    capture_start_offset = (audio_zero - video_zero) / frequency
    for audio_time in tone_events:
        expected_video = audio_time + capture_start_offset
        if not remaining_video:
            break
        video_index = min(remaining_video, key=lambda i: abs(flash_events[i] - expected_video))
        video_time = flash_events[video_index]
        offset_ms = ((audio_zero - video_zero) / frequency + audio_time - video_time) * 1000
        if abs(offset_ms) <= 250:
            matches.append((video_time, audio_time, offset_ms))
            remaining_video.remove(video_index)
    if matches:
        x = np.array([(a + b) / 2 for a, b, _ in matches], dtype=np.float64)
        y = np.array([c for _, _, c in matches], dtype=np.float64)
        slope_ms_per_min = float(np.polyfit(x, y, 1)[0] * 60)
    else:
        slope_ms_per_min = None
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("w", encoding="utf-8", newline="") as f:
        writer = csv.writer(f)
        writer.writerow(["video_flash_seconds", "audio_tone_seconds", "audio_minus_video_ms"])
        writer.writerows([[f"{v:.6f}", f"{a:.6f}", f"{o:.3f}"] for v, a, o in matches])
    report = {"video": str(media), "audio": str(audio_path), "video_frames": len(video_times),
              "flash_events": len(flash_events), "tone_events": len(tone_events), "matched_events": len(matches),
              "median_audio_minus_video_ms": float(np.median([m[2] for m in matches])) if matches else None,
              "p95_abs_audio_minus_video_ms": float(np.percentile(np.abs([m[2] for m in matches]), 95)) if matches else None,
              "drift_ms_per_minute": slope_ms_per_min, "first_match": matches[0] if matches else None,
              "last_match": matches[-1] if matches else None, "analysis_csv": str(output)}
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
