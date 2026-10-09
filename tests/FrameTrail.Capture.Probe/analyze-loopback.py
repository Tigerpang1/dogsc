"""Measure 440 Hz pulse timing in an independent WASAPI WAV against Stopwatch ticks."""
import csv
import json
import pathlib
import subprocess
import sys

import numpy as np


def run(args):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)


def main():
    if len(sys.argv) != 5:
        raise SystemExit("Usage: analyze-loopback.py <ffmpeg.exe> <capture.wav> <pulse.csv> <analysis.csv>")
    ffmpeg, wav, pulse_csv, output_csv = sys.argv[1:]
    wav = pathlib.Path(wav)
    summary = json.loads(wav.with_suffix(".summary.json").read_text(encoding="utf-8"))
    handle = summary["Handle"]
    start_ticks = int(handle["StartStopwatchTicks"])
    frequency = int(handle["StopwatchFrequency"])
    raw = run([ffmpeg, "-v", "error", "-i", str(wav), "-vn", "-af",
               "pan=mono|c0=0.5*c0+0.5*c1,bandpass=f=440:width_type=h:width=120,aresample=4000",
               "-f", "f32le", "-ac", "1", "-"]).stdout
    samples = np.frombuffer(raw, dtype="<f4")
    window = 80
    hop = 20
    rms = np.array([np.sqrt(np.mean(np.square(samples[i:i + window], dtype=np.float64)))
                    for i in range(0, len(samples) - window + 1, hop)], dtype=np.float64)
    sample_times = np.arange(len(rms), dtype=np.float64) * hop / 4000
    pulses = list(csv.DictReader(pathlib.Path(pulse_csv).open(encoding="utf-8-sig", newline="")))
    rows = []
    for pulse in pulses:
        expected = (int(pulse["stopwatch_ticks"]) - start_ticks) / frequency
        if expected < 0 or expected > (len(samples) / 4000):
            continue
        mask = (sample_times >= expected - 0.8) & (sample_times <= expected + 0.8)
        if not np.any(mask):
            continue
        times = sample_times[mask]
        signal = rms[mask]
        baseline = float(np.median(signal))
        peak_index = int(np.argmax(signal))
        peak = float(signal[peak_index])
        onset = None
        if peak - baseline >= 0.0002:
            threshold = baseline + (peak - baseline) * 0.5
            first = peak_index
            while first > 0 and signal[first - 1] >= threshold:
                first -= 1
            onset = float(times[first])
        delta = None if onset is None else (onset - expected) * 1000
        rows.append({"pulse": pulse["pulse"], "expected_seconds_from_handle": f"{expected:.6f}",
                     "detected_audio_onset_seconds": "" if onset is None else f"{onset:.6f}",
                     "audio_minus_expected_ms": "" if delta is None else f"{delta:.3f}",
                     "bandpass_peak_rms": f"{peak:.8f}", "baseline_rms": f"{baseline:.8f}"})
    with pathlib.Path(output_csv).open("w", encoding="utf-8", newline="") as handle_file:
        writer = csv.DictWriter(handle_file, fieldnames=rows[0].keys())
        writer.writeheader()
        writer.writerows(rows)
    offsets = np.array([float(row["audio_minus_expected_ms"]) for row in rows if row["audio_minus_expected_ms"]], dtype=np.float64)
    if len(offsets):
        detected_x = np.array([float(row["expected_seconds_from_handle"]) for row in rows if row["audio_minus_expected_ms"]], dtype=np.float64)
        drift_ms_per_minute = float(np.polyfit(detected_x, offsets, 1)[0] * 60)
    else:
        drift_ms_per_minute = None
    print(json.dumps({"wav": str(wav), "pulse_count_in_range": len(rows), "matched_count": len(offsets),
                      "median_audio_minus_expected_ms": float(np.median(offsets)) if len(offsets) else None,
                      "p95_abs_audio_minus_expected_ms": float(np.percentile(np.abs(offsets), 95)) if len(offsets) else None,
                      "linear_drift_ms_per_minute": drift_ms_per_minute,
                      "first_offset_ms": float(offsets[0]) if len(offsets) else None,
                      "last_offset_ms": float(offsets[-1]) if len(offsets) else None,
                      "analysis_csv": str(output_csv)}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
