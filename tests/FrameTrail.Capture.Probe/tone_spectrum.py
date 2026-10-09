"""Shared spectral classifier for the sync fixture and its deterministic controls."""
import numpy as np


RULE_VERSION = "fixture-1khz-band-ratio-v1"
SAMPLE_RATE_HZ = 8000
WINDOW_DURATION_MS = 100
WINDOW_SAMPLES = SAMPLE_RATE_HZ * WINDOW_DURATION_MS // 1000
TARGET_BAND_HZ = (980, 1020)
COMPARISON_BAND_HZ = (600, 1400)
MIN_TARGET_POWER_FRACTION = 0.85
STABLE_ENVELOPE_POLICY_VERSION = "fixture-1khz-envelope-stable-60ms-v1"
STABLE_ENVELOPE_WINDOW_MS = 60
ENVELOPE_TILE_MS = 10
ENVELOPE_SEARCH_STEP_MS = 5


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


def locate_stable_envelope_window(samples, candidate_start_sample, candidate_duration_samples,
                                  sample_rate_hz=SAMPLE_RATE_HZ):
    """Return the earliest 60 ms window with six active 10 ms envelope tiles.

    The onset/duration come from the existing audio-only bandpassed candidate detector.
    The classification window is selected from PCM amplitude only; no spectrum score or
    video timing participates in its placement. The event timestamp remains the detector
    onset and is not changed by this helper.
    """
    source = np.asarray(samples, dtype=np.float64)
    if candidate_start_sample < 0 or candidate_duration_samples <= 0:
        return None
    tile = max(1, round(sample_rate_hz * ENVELOPE_TILE_MS / 1000))
    window_samples = max(tile, round(sample_rate_hz * STABLE_ENVELOPE_WINDOW_MS / 1000))
    step = max(1, round(sample_rate_hz * ENVELOPE_SEARCH_STEP_MS / 1000))
    onset = int(candidate_start_sample)
    end = min(len(source), onset + int(candidate_duration_samples))
    if end - onset < window_samples:
        return None

    baseline = []
    baseline_start = max(0, onset - round(sample_rate_hz * 0.250))
    baseline_end = max(baseline_start, onset - round(sample_rate_hz * 0.020))
    for start in range(baseline_start, baseline_end - tile + 1, tile):
        chunk = source[start:start + tile]
        if len(chunk) == tile:
            baseline.append(float(np.sqrt(np.mean(chunk * chunk))))
    baseline_rms = float(np.median(baseline)) if baseline else 0.0

    active = []
    for start in range(onset, end - tile + 1, tile):
        chunk = source[start:start + tile]
        if len(chunk) == tile:
            active.append(float(np.sqrt(np.mean(chunk * chunk))))
    if not active:
        return None
    p90_active_rms = float(np.percentile(active, 90))
    threshold = baseline_rms + 0.5 * (p90_active_rms - baseline_rms)
    if p90_active_rms <= baseline_rms:
        return None

    last_start = min(len(source), end) - window_samples
    for start in range(onset, last_start + 1, step):
        tile_rms = []
        for tile_index in range(window_samples // tile):
            chunk_start = start + tile_index * tile
            chunk = source[chunk_start:chunk_start + tile]
            if len(chunk) != tile:
                tile_rms = []
                break
            tile_rms.append(float(np.sqrt(np.mean(chunk * chunk))))
        if len(tile_rms) == window_samples // tile and min(tile_rms) >= threshold:
            return {
                "start_sample": start,
                "window_samples": window_samples,
                "window_ms": window_samples * 1000.0 / sample_rate_hz,
                "offset_ms": (start - onset) * 1000.0 / sample_rate_hz,
                "baseline_rms": baseline_rms,
                "p90_active_rms": p90_active_rms,
                "threshold_rms": threshold,
                "tile_rms": tile_rms,
            }
    return None
