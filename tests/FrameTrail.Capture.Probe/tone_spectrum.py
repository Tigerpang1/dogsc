"""Shared spectral classifier for the sync fixture and its deterministic controls."""
import numpy as np


RULE_VERSION = "fixture-1khz-band-ratio-v1"
SAMPLE_RATE_HZ = 8000
WINDOW_DURATION_MS = 100
WINDOW_SAMPLES = SAMPLE_RATE_HZ * WINDOW_DURATION_MS // 1000
TARGET_BAND_HZ = (980, 1020)
COMPARISON_BAND_HZ = (600, 1400)
MIN_TARGET_POWER_FRACTION = 0.85


def target_power_fraction(samples, sample_rate_hz=SAMPLE_RATE_HZ):
    centered = np.asarray(samples, dtype=np.float64)
    if centered.size == 0:
        return 0.0
    centered = centered - centered.mean()
    spectrum = np.abs(np.fft.rfft(centered * np.hanning(len(centered)))) ** 2
    frequencies = np.fft.rfftfreq(len(centered), 1 / sample_rate_hz)
    comparison = spectrum[(frequencies >= COMPARISON_BAND_HZ[0]) & (frequencies <= COMPARISON_BAND_HZ[1])].sum()
    target = spectrum[(frequencies >= TARGET_BAND_HZ[0]) & (frequencies <= TARGET_BAND_HZ[1])].sum()
    return float(target / comparison) if comparison > 0 else 0.0
