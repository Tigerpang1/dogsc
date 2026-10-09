"""将本地440Hz脉冲WAV与录制中可见测试窗口的闪白事件对齐分析。

需要 Python 3、numpy 与 FFmpeg/ffprobe。每个脉冲输出一行，记录编码流内的事件起始偏差；
该值用于观察媒体流对齐情况，不表示人的感知延迟、端点延迟或绝对首帧时钟。
"""
import csv
import datetime as dt
import json
import pathlib
import subprocess
import sys

import numpy as np


def run(args):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)


def utc(value):
    return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))


def get_video_timestamps(ffprobe, media):
    output = run([ffprobe, "-v", "error", "-select_streams", "v:0", "-show_frames",
                  "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", str(media)]).stdout
    return np.array([float(line) for line in output.decode("ascii", "ignore").splitlines() if line.strip()], dtype=np.float64)


def detect_video(ffmpeg, media):
    raw = run([ffmpeg, "-v", "error", "-i", str(media), "-vf",
               "crop=960:540:200:150,scale=16:9:flags=area,format=gray", "-an", "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "gray", "-"]).stdout
    frame_size = 16 * 9
    if len(raw) % frame_size:
        raise RuntimeError("Unexpected raw video byte count")
    levels = np.frombuffer(raw, dtype=np.uint8).reshape((-1, frame_size)).mean(axis=1)
    return levels


def detect_audio(ffmpeg, media):
    raw = run([ffmpeg, "-v", "error", "-i", str(media), "-vn", "-af",
               "pan=mono|c0=0.5*c0+0.5*c1,bandpass=f=440:width_type=h:width=120,aresample=4000",
               "-f", "f32le", "-ac", "1", "-"]).stdout
    samples = np.frombuffer(raw, dtype="<f4")
    window = 80  # 20 ms at 4 kHz
    hop = 20     # 5 ms step
    if len(samples) < window:
        return np.empty(0), np.empty(0)
    rms = np.array([np.sqrt(np.mean(np.square(samples[i:i + window], dtype=np.float64)))
                    for i in range(0, len(samples) - window + 1, hop)], dtype=np.float64)
    times = np.arange(len(rms), dtype=np.float64) * hop / 4000
    return times, rms


def onset(times, signal, expected, radius=2.0, minimum_delta=1.0):
    near = (times >= expected - radius) & (times <= expected + radius)
    if not np.any(near):
        return None, None
    segment = signal[near]
    segment_times = times[near]
    baseline = float(np.median(segment))
    peak = float(np.max(segment))
    if peak - baseline < minimum_delta:
        return None, peak
    threshold = baseline + (peak - baseline) * 0.5
    peak_index = int(np.argmax(segment))
    onset_index = peak_index
    while onset_index > 0 and segment[onset_index - 1] >= threshold:
        onset_index -= 1
    return float(segment_times[onset_index]), peak


def main():
    if len(sys.argv) not in (6, 7):
        raise SystemExit("Usage: analyze-sync.py <ffmpeg> <ffprobe> <capture.mp4> <pulse.csv> <output.csv> [independent-loopback.wav]")
    ffmpeg, ffprobe, media, pulse_csv, output_csv = sys.argv[1:6]
    audio_media = pathlib.Path(sys.argv[6]) if len(sys.argv) == 7 else pathlib.Path(media)
    media = pathlib.Path(media)
    summary_path = media.with_suffix(".summary.json")
    if not summary_path.exists():
        raise SystemExit(f"Missing capture summary: {summary_path}")
    start_utc = utc(json.loads(summary_path.read_text(encoding="utf-8"))["StartedUtc"])
    audio_start_utc = start_utc
    if audio_media != pathlib.Path(media):
        audio_summary = json.loads(audio_media.with_suffix(".summary.json").read_text(encoding="utf-8"))
        audio_start_utc = utc(audio_summary["StartedUtc"])
    pulses = list(csv.DictReader(pathlib.Path(pulse_csv).open(encoding="utf-8-sig", newline="")))
    video_times = get_video_timestamps(ffprobe, media)
    video_levels = detect_video(ffmpeg, media)
    audio_times, audio_levels = detect_audio(ffmpeg, audio_media)
    if len(video_times) != len(video_levels):
        raise RuntimeError(f"Video timestamps/frames differ: {len(video_times)} vs {len(video_levels)}")
    rows = []
    for pulse in pulses:
        expected = (utc(pulse["utc"]) - start_utc).total_seconds()
        video_onset, video_peak = onset(video_times, video_levels, expected, minimum_delta=5.0)
        expected_audio = (utc(pulse["utc"]) - audio_start_utc).total_seconds()
        audio_onset, audio_peak = onset(audio_times, audio_levels, expected_audio, minimum_delta=0.00005)
        audio_in_video_timeline = None if audio_onset is None else audio_onset + (audio_start_utc - start_utc).total_seconds()
        rows.append({"pulse": pulse["pulse"], "expected_media_seconds": f"{expected:.6f}",
                     "video_onset_seconds": "" if video_onset is None else f"{video_onset:.6f}",
                     "audio_onset_video_timeline_seconds": "" if audio_in_video_timeline is None else f"{audio_in_video_timeline:.6f}",
                     "audio_minus_video_ms": "" if video_onset is None or audio_in_video_timeline is None else f"{(audio_in_video_timeline-video_onset)*1000:.3f}",
                     "video_peak_gray": "" if video_peak is None else f"{video_peak:.3f}",
                     "audio_bandpass_peak_rms": "" if audio_peak is None else f"{audio_peak:.8f}"})
    with pathlib.Path(output_csv).open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(rows)
    matched = [float(row["audio_minus_video_ms"]) for row in rows if row["audio_minus_video_ms"]]
    print(json.dumps({"pulse_count": len(rows), "matched_count": len(matched), "audio_media": str(audio_media),
                      "median_audio_minus_video_ms": float(np.median(matched)) if matched else None,
                      "p95_abs_audio_minus_video_ms": float(np.percentile(np.abs(matched), 95)) if matched else None,
                      "analysis_csv": str(output_csv)}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
