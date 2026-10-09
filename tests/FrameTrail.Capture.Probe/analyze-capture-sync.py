"""Compare captured flash and WASAPI tone onsets against shared Stopwatch pulse ticks."""
import csv
import json
import pathlib
import subprocess
import sys

import numpy as np


def run(args):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)


def video_onsets(ffmpeg, ffprobe, media):
    pts = run([ffprobe, "-v", "error", "-select_streams", "v:0", "-show_frames",
               "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", str(media)]).stdout
    times = np.array([float(line) for line in pts.decode("ascii", "ignore").splitlines() if line.strip()], dtype=np.float64)
    raw = run([ffmpeg, "-v", "error", "-i", str(media), "-vf", "scale=16:9:flags=area,format=gray",
               "-an", "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "gray", "-"]).stdout
    pixels_per_frame = 16 * 9
    if len(raw) % pixels_per_frame:
        raise RuntimeError("unexpected raw video byte count")
    levels = np.frombuffer(raw, dtype=np.uint8).reshape((-1, pixels_per_frame)).mean(axis=1)
    if len(times) != len(levels):
        raise RuntimeError(f"video timestamp/frame count mismatch: {len(times)} vs {len(levels)}")
    return times, levels


def audio_signal(ffmpeg, media):
    raw = run([ffmpeg, "-v", "error", "-i", str(media), "-vn", "-af",
               "pan=mono|c0=0.5*c0+0.5*c1,bandpass=f=440:width_type=h:width=120,aresample=4000",
               "-f", "f32le", "-ac", "1", "-"]).stdout
    samples = np.frombuffer(raw, dtype="<f4")
    window, hop, rate = 80, 20, 4000
    rms = np.array([np.sqrt(np.mean(np.square(samples[i:i + window], dtype=np.float64)))
                    for i in range(0, len(samples) - window + 1, hop)], dtype=np.float64)
    return np.arange(len(rms), dtype=np.float64) * hop / rate, rms


def find_onset(times, values, expected, radius, threshold_delta):
    mask = (times >= expected - radius) & (times <= expected + radius)
    if not np.any(mask):
        return None
    indices = np.flatnonzero(mask)
    signal = values[indices]
    baseline = float(np.median(signal))
    peak_index = int(np.argmax(signal))
    peak = float(signal[peak_index])
    if peak - baseline < threshold_delta:
        return None
    threshold = baseline + (peak - baseline) * 0.5
    onset_index = peak_index
    while onset_index > 0 and signal[onset_index - 1] >= threshold:
        onset_index -= 1
    return float(times[indices[onset_index]])


def main():
    if len(sys.argv) != 5:
        raise SystemExit("Usage: analyze-capture-sync.py <ffmpeg.exe> <ffprobe.exe> <capture.mp4> <analysis.csv>")
    ffmpeg, ffprobe, media_arg, output_arg = sys.argv[1:]
    media, output = pathlib.Path(media_arg), pathlib.Path(output_arg)
    summary = json.loads(media.with_suffix(".summary.json").read_text(encoding="utf-8"))
    frequency = int(summary["StopwatchFrequency"])
    video_zero = int(summary["FirstFrameObservedStopwatchTicks"])
    audio_zero = int(summary["AudioStartStopwatchTicks"])
    audio_path = pathlib.Path(summary["AudioPath"])
    pulse_path = pathlib.Path(summary["PulsePath"])
    video_times, video_levels = video_onsets(ffmpeg, ffprobe, media)
    audio_times, audio_levels = audio_signal(ffmpeg, audio_path)
    rows = []
    with pulse_path.open(encoding="utf-8-sig", newline="") as file:
        for pulse in csv.DictReader(file):
            ticks = int(pulse["stopwatch_ticks"])
            expected_video = (ticks - video_zero) / frequency
            expected_audio = (ticks - audio_zero) / frequency
            video = find_onset(video_times, video_levels, expected_video, 0.8, 8.0)
            audio = find_onset(audio_times, audio_levels, expected_audio, 0.8, 0.00005)
            v_error = None if video is None else (video - expected_video) * 1000
            a_error = None if audio is None else (audio - expected_audio) * 1000
            rows.append({"pulse": pulse["pulse"], "expected_video_seconds": f"{expected_video:.6f}",
                         "video_onset_seconds": "" if video is None else f"{video:.6f}",
                         "video_minus_trigger_ms": "" if v_error is None else f"{v_error:.3f}",
                         "expected_audio_seconds": f"{expected_audio:.6f}",
                         "audio_onset_seconds": "" if audio is None else f"{audio:.6f}",
                         "audio_minus_trigger_ms": "" if a_error is None else f"{a_error:.3f}",
                         "audio_minus_video_ms": "" if a_error is None or v_error is None else f"{a_error-v_error:.3f}"})
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("w", encoding="utf-8", newline="") as file:
        writer = csv.DictWriter(file, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(rows)
    matched = np.array([float(row["audio_minus_video_ms"]) for row in rows if row["audio_minus_video_ms"]], dtype=np.float64)
    print(json.dumps({"capture": str(media), "audio": str(audio_path), "pulses": len(rows), "matched": len(matched),
                      "median_av_offset_ms": float(np.median(matched)) if len(matched) else None,
                      "p95_abs_av_offset_ms": float(np.percentile(np.abs(matched), 95)) if len(matched) else None,
                      "av_offsets_ms": matched.tolist(), "analysis_csv": str(output)}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
