"""Deterministic synthetic controls for the sync fixture's 1 kHz spectral signature."""
import json

import numpy as np
from tone_spectrum import (
    COMPARISON_BAND_HZ,
    MIN_TARGET_POWER_FRACTION,
    RULE_VERSION,
    SAMPLE_RATE_HZ,
    TARGET_BAND_HZ,
    WINDOW_DURATION_MS,
    WINDOW_SAMPLES,
    target_power_fraction,
)


def tone(frequency, phase=0.0):
    time = np.arange(WINDOW_SAMPLES) / SAMPLE_RATE_HZ
    return 0.5 * np.sin(2 * np.pi * frequency * time + phase)


def main():
    time = np.arange(WINDOW_SAMPLES) / SAMPLE_RATE_HZ
    rng = np.random.default_rng(271828)
    chirp = 0.5 * np.sin(2 * np.pi * (800 * time + 0.5 * (400 / time[-1]) * time**2))
    signals = {
        "positive_1000hz": tone(1000),
        "negative_840hz": tone(840),
        "negative_720hz": tone(720),
        "negative_broadband_noise": rng.normal(0, 0.5, WINDOW_SAMPLES),
        "negative_nonstationary_800_to_1200hz_chirp": chirp,
        "negative_dual_tone_1000_plus_840hz": tone(1000) + tone(840, np.pi / 3),
    }
    result = {
        name: {
            "target_power_fraction": target_power_fraction(samples),
            "accepted": target_power_fraction(samples) >= MIN_TARGET_POWER_FRACTION,
        }
        for name, samples in signals.items()
    }
    expectations = {
        "positive_1000hz": True,
        "negative_840hz": False,
        "negative_720hz": False,
        "negative_broadband_noise": False,
        "negative_nonstationary_800_to_1200hz_chirp": False,
        "negative_dual_tone_1000_plus_840hz": False,
    }
    failures = [name for name, expected in expectations.items() if result[name]["accepted"] != expected]
    print(json.dumps({
        "rule_version": RULE_VERSION,
        "sample_rate_hz": SAMPLE_RATE_HZ,
        "fft_window_ms": WINDOW_DURATION_MS,
        "fft_bin_width_hz": SAMPLE_RATE_HZ / WINDOW_SAMPLES,
        "target_band_hz": TARGET_BAND_HZ,
        "comparison_band_hz": COMPARISON_BAND_HZ,
        "minimum_target_power_fraction": MIN_TARGET_POWER_FRACTION,
        "controls": result,
        "passed": not failures,
        "failures": failures,
    }, indent=2))
    if failures:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
