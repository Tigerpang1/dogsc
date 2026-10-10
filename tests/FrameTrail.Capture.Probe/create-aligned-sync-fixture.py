"""Create an independent five-second capture stimulus without product export filters."""
import argparse
import array
import hashlib
import json
import math
from pathlib import Path
import subprocess
import wave
import numpy as np


def verify_encoded_fixture(ffmpeg, media, cues, fps, rate):
    def decode(arguments):
        return subprocess.run([ffmpeg, "-v", "error", "-i", str(media), *arguments],
                              check=True, capture_output=True).stdout

    pixels = np.frombuffer(decode(["-an", "-vf", "scale=16:9:flags=area,format=gray",
                                  "-f", "rawvideo", "-pix_fmt", "gray", "-"]), dtype=np.uint8)
    bright = pixels.reshape((-1, 144)).mean(axis=1) > 127
    video_starts = (np.flatnonzero(bright & ~np.r_[False, bright[:-1]]) / fps).tolist()
    audio = np.frombuffer(decode(["-vn", "-ac", "1", "-ar", str(rate), "-f", "f32le", "-"]), dtype="<f4")
    active = np.flatnonzero(np.abs(audio) > 0.02)
    groups = np.split(active, np.flatnonzero(np.diff(active) > rate * 0.05) + 1)
    audio_starts = [float(group[0] / rate) for group in groups if len(group) and group[-1] - group[0] > rate * 0.05]
    offsets = [(audio_time - video_time) * 1000 for audio_time, video_time in zip(audio_starts, video_starts)]
    passed = (len(video_starts) == len(audio_starts) == len(cues)
              and all(abs(actual - expected) < 1 / fps for actual, expected in zip(video_starts, cues))
              and all(abs(offset) <= 3 for offset in offsets))
    return {"method": "Decoded video threshold 127; decoded PCM amplitude threshold 0.02, tone groups separated by 50 ms",
            "video_starts_seconds": video_starts, "audio_starts_seconds": audio_starts,
            "audio_minus_video_ms": offsets, "max_allowed_absolute_ms": 3, "passed": passed}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("ffmpeg")
    parser.add_argument("output_directory", type=Path)
    args = parser.parse_args()
    root = args.output_directory.resolve()
    root.mkdir(parents=True, exist_ok=False)
    rate, duration, fps = 48000, 5, 60
    cues = [0.5, 2.0, 4.0]
    wave_path = root / "aligned-tone.wav"
    pcm = array.array("h", [0]) * (rate * duration * 2)
    for cue in cues:
        for frame in range(round(cue * rate), round((cue + 0.1) * rate)):
            value = round(0.2 * 32767 * math.sin(2 * math.pi * 1000 * (frame / rate)))
            pcm[frame * 2] = pcm[frame * 2 + 1] = value
    with wave.open(str(wave_path), "wb") as output:
        output.setparams((2, 2, rate, 0, "NONE", "not compressed"))
        output.writeframes(pcm.tobytes())
    enabled = "+".join(f"gte(t,{cue})*lt(t,{cue + 0.1})" for cue in cues)
    video = f"color=c=black:s=1920x1080:r={fps}:d={duration},drawbox=color=white:t=fill:enable='{enabled}'"
    media = root / "aligned-sync.mp4"
    command = [str(Path(args.ffmpeg).resolve()), "-v", "error", "-f", "lavfi", "-i", video,
               "-i", str(wave_path), "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12",
               "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-t", str(duration),
               "-movflags", "+faststart", "-n", str(media)]
    subprocess.run(command, check=True, capture_output=True)
    hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in (wave_path, media)}
    verification = verify_encoded_fixture(command[0], media, cues, fps, rate)
    metadata = {"schema_version": 1, "purpose": "Independent aligned capture stimulus; no FrameTrail export/filter path",
                "duration_seconds": duration, "frame_rate": fps, "sample_rate": rate,
                "cue_starts_seconds": cues, "cue_duration_seconds": 0.1, "tone_hz": 1000,
                "source_audio_start_frames": [round(cue * rate) for cue in cues],
                "source_video_start_frames": [round(cue * fps) for cue in cues],
                "command": command, "sha256": hashes,
                "verification": verification}
    (root / "fixture-provenance.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    (root / "analysis-input.json").write_text(json.dumps({"AudioPath": str(media),
        "AudioStartStopwatchTicks": 0, "FirstFrameObservedStopwatchTicks": 0, "StopwatchFrequency": 1}, indent=2), encoding="utf-8")
    print(str(media))
    if not verification["passed"]:
        raise SystemExit("Encoded stimulus failed alignment verification; do not use it for capture acceptance.")


if __name__ == "__main__":
    main()
