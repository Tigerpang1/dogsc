"""Deterministic controls for the fixed 60 ms envelope-located tone window."""
import json
from pathlib import Path

import numpy as np

from tone_spectrum import (
    MIN_TARGET_POWER_FRACTION,
    SAMPLE_RATE_HZ,
    locate_stable_envelope_window,
    target_power_fraction,
)


def synthesize(rate, frequency, amplitude=0.03, duration_ms=130, onset_seconds=0.35,
               phase=0.7, extra_frequency=None, noise_amplitude=0.0):
    total_seconds = 0.9
    times = np.arange(round(total_seconds * rate), dtype=np.float64) / rate
    relative = times - onset_seconds
    active = (relative >= 0) & (relative < duration_ms / 1000)
    envelope = np.zeros_like(times)
    active_relative = relative[active]
    attack = np.minimum(1.0, active_relative / 0.005)
    release = np.minimum(1.0, (duration_ms / 1000 - active_relative) / 0.005)
    envelope[active] = np.maximum(0.0, np.minimum(attack, release))
    signal = amplitude * envelope * np.sin(2 * np.pi * frequency * relative + phase)
    if extra_frequency is not None:
        signal += amplitude * envelope * np.sin(2 * np.pi * extra_frequency * relative + phase * 0.5)
    if noise_amplitude:
        rng = np.random.default_rng(81173)
        signal += noise_amplitude * rng.standard_normal(len(signal)) * envelope
    target_times = np.arange(round(total_seconds * SAMPLE_RATE_HZ), dtype=np.float64) / SAMPLE_RATE_HZ
    resampled = np.interp(target_times, times, signal).astype(np.float32)
    candidate_onset = max(0, round((onset_seconds - 0.010) * SAMPLE_RATE_HZ))
    candidate_duration = round((duration_ms + 10) * SAMPLE_RATE_HZ / 1000)
    selected = locate_stable_envelope_window(resampled, candidate_onset, candidate_duration)
    if selected is None:
        return {"found": False, "ratio": 0.0, "offset_ms": None}
    window = resampled[selected["start_sample"]:selected["start_sample"] + selected["window_samples"]]
    return {"found": True, "ratio": target_power_fraction(window),
            "offset_ms": selected["offset_ms"], "window_samples": selected["window_samples"]}


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def main():
    cases = []
    for rate in (44100, 48000):
        for pulse_ms in (70, 130):
            for phase_offset_ms in (0.25, 3.75):
                result = synthesize(rate, 1000, amplitude=0.03, duration_ms=pulse_ms,
                                    onset_seconds=0.35 + phase_offset_ms / 1000)
                cases.append({"kind": "positive", "rate": rate, "duration_ms": pulse_ms,
                              "phase_offset_ms": phase_offset_ms, **result})
                require(result["found"] and result["ratio"] >= MIN_TARGET_POWER_FRACTION,
                        f"1 kHz positive failed: {cases[-1]}")

    amplitude_checks = [synthesize(48000, 1000, amplitude=amplitude, duration_ms=130)
                        for amplitude in (0.0001, 0.003, 0.03, 0.3)]
    for result in amplitude_checks:
        require(result["found"] and result["ratio"] >= MIN_TARGET_POWER_FRACTION,
                f"Amplitude-scaled 1 kHz positive failed: {result}")
    require(max(result["ratio"] for result in amplitude_checks) - min(result["ratio"] for result in amplitude_checks) < 0.01,
            "Normalized spectral classification should remain nearly invariant to amplitude scaling.")

    negatives = {
        "840_hz": synthesize(48000, 840),
        "720_hz": synthesize(44100, 720),
        "970_hz_near_band": synthesize(48000, 970),
        "1030_hz_near_band": synthesize(44100, 1030),
        "960_hz": synthesize(48000, 960),
        "1040_hz": synthesize(44100, 1040),
        "equal_1000_plus_840_hz": synthesize(48000, 1000, extra_frequency=840),
        "broadband_noise": synthesize(48000, 1000, amplitude=0, noise_amplitude=0.03),
    }
    for label, result in negatives.items():
        require(not result["found"] or result["ratio"] < MIN_TARGET_POWER_FRACTION,
                f"Negative control unexpectedly classified as the fixture tone: {label} {result}")

    truncated = synthesize(48000, 1000, duration_ms=45)
    require(not truncated["found"], f"A 45 ms truncated pulse must not produce a 60 ms stable window: {truncated}")
    report = {
        "policy": "fixture-1khz-envelope-stable-60ms-v1",
        "threshold": MIN_TARGET_POWER_FRACTION,
        "positive_controls": cases,
        "amplitude_invariance_controls": amplitude_checks,
        "negative_controls": negatives,
        "truncated_pulse_45ms": truncated,
        "result": "passed",
    }
    output = Path(__file__).resolve().parents[2] / "artifacts/validation/capture-qpc-session-restore-a2be517/tone-window-policy-selftest.json"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
