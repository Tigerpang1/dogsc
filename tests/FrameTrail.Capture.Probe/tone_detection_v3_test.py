"""Regression controls for p99 candidate gating plus unchanged full spectral classification."""
import json
from pathlib import Path

import numpy as np

from tone_detection_v3 import detect_audio_tones
from tone_spectrum import SAMPLE_RATE_HZ


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def pulse_pair(duration_seconds, pulse_seconds=0.115, amplitude_rms=0.0033):
    count = round(duration_seconds * SAMPLE_RATE_HZ)
    raw = np.zeros(count, dtype=np.float32)
    bandpassed = np.zeros(count, dtype=np.float32)
    pulse_period = 5 / 3
    pulse_count = max(1, int((duration_seconds - 0.5) // pulse_period) + 1)
    pulse_samples = round(pulse_seconds * SAMPLE_RATE_HZ)
    times = np.arange(pulse_samples, dtype=np.float64) / SAMPLE_RATE_HZ
    envelope = np.ones(pulse_samples, dtype=np.float64)
    ramp = max(1, round(0.005 * SAMPLE_RATE_HZ))
    envelope[:ramp] = np.linspace(0, 1, ramp, endpoint=False)
    envelope[-ramp:] = np.linspace(1, 0, ramp, endpoint=False)
    tone = (amplitude_rms * np.sqrt(2) * np.sin(2 * np.pi * 1000 * times) * envelope).astype(np.float32)
    starts = []
    for index in range(pulse_count):
        start = round((0.5 + index * (5 / 3)) * SAMPLE_RATE_HZ)
        if start + len(tone) > count:
            break
        raw[start:start + len(tone)] += tone
        bandpassed[start:start + len(tone)] += tone
        starts.append(start / SAMPLE_RATE_HZ)
    return bandpassed, raw, starts


def add_wideband_outlier(bandpassed, raw, at_seconds=20.2, target_rms=0.099, duration_seconds=0.135):
    rng = np.random.default_rng(742901)
    count = round(duration_seconds * SAMPLE_RATE_HZ)
    start = round(at_seconds * SAMPLE_RATE_HZ)
    noise = rng.standard_normal(count)
    noise *= target_rms / np.sqrt(np.mean(noise * noise))
    bandpassed[start:start + count] += noise.astype(np.float32)
    raw[start:start + count] += noise.astype(np.float32)


def analyze_pair(bandpassed, raw):
    return detect_audio_tones(bandpassed, raw, SAMPLE_RATE_HZ)


def main():
    short_band, short_raw, short_starts = pulse_pair(30)
    short = analyze_pair(short_band, short_raw)
    require(len(short["tone_events"]) == 18, f"Short sparse signal must classify all 18 tones: {len(short['tone_events'])}")

    long_band, long_raw, _ = pulse_pair(1800)
    add_wideband_outlier(long_band, long_raw)
    long = analyze_pair(long_band, long_raw)
    require(len(long["tone_events"]) == 1080, f"Long sparse signal plus one wideband outlier must classify all 1080 tones: {len(long['tone_events'])}")
    require(np.allclose(long["tone_events"][:18], short["tone_events"], atol=0.005),
            "The first 18 tone detections must keep the same event starts in short and long inputs")
    require(long["statistics"]["maximum_rms"] > 25 * long["statistics"]["p99_rms"],
            "The synthetic outlier must be a meaningful max-RMS stress case")
    require(long["statistics"]["actual_threshold_rms"] < 2 * short["statistics"]["actual_threshold_rms"],
            "One outlier must not inflate the p99 candidate gate")
    require(any(not c["accepted_as_1khz_fixture_tone"] for c in long["candidates"]),
            "The wideband peak must be a candidate that the full tone_spectrum classifier rejects")

    silent = analyze_pair(np.zeros(2 * SAMPLE_RATE_HZ, dtype=np.float32), np.zeros(2 * SAMPLE_RATE_HZ, dtype=np.float32))
    require(not silent["tone_events"], "Full silence must not produce tone events")

    rng = np.random.default_rng(99281)
    broadband = rng.normal(0, 0.003, 2 * SAMPLE_RATE_HZ).astype(np.float32)
    noise = analyze_pair(broadband, broadband)
    require(not noise["tone_events"], "Steady broadband noise must not classify as a fixture tone")

    short_band, short_raw, _ = pulse_pair(1.0, pulse_seconds=0.035)
    short_only = analyze_pair(short_band, short_raw)
    require(not short_only["tone_events"], "A 35 ms pulse must fail the fixed 60 ms stable-envelope classifier")

    result = {
        "policy": long["statistics"],
        "short_sparse_tones": len(short["tone_events"]),
        "long_sparse_tones_with_wideband_outlier": len(long["tone_events"]),
        "short_long_first_event_starts_equal": True,
        "rejected_wideband_candidates": sum(not c["accepted_as_1khz_fixture_tone"] for c in long["candidates"]),
        "silent_tones": len(silent["tone_events"]),
        "broadband_noise_tones": len(noise["tone_events"]),
        "35ms_pulse_tones": len(short_only["tone_events"]),
        "result": "passed",
    }
    output = Path(__file__).resolve().parents[2] / "artifacts/validation/tone-detection-v3-selftest.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
