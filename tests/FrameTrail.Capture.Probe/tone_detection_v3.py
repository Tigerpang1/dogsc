"""Robust global candidate gate for fixture sync tones; spectral policy remains shared with v2."""
import numpy as np

from tone_spectrum import (
    ENVELOPE_SEARCH_STEP_MS,
    ENVELOPE_TILE_MS,
    MIN_TARGET_POWER_FRACTION,
    SAMPLE_RATE_HZ,
    STABLE_ENVELOPE_POLICY_VERSION,
    STABLE_ENVELOPE_WINDOW_MS,
    TARGET_BAND_HZ,
    COMPARISON_BAND_HZ,
    locate_stable_envelope_window,
    target_power_fraction,
)


POLICY_VERSION = "global-window-rms-p99-candidate-gate-v1"
RMS_PERCENTILE = 99.0
MINIMUM_RMS_RISE = 0.00003
THRESHOLD_FRACTION = 0.15
WINDOW_MS = 20
HOP_MS = 5
MIN_CANDIDATE_WINDOWS = 8
MAX_INTER_WINDOW_GAP = 10


def window_rms(samples, sample_rate=SAMPLE_RATE_HZ):
    """Return 20 ms RMS windows every 5 ms without materializing an overlapping-window matrix."""
    source = np.asarray(samples, dtype=np.float32)
    width = max(1, round(sample_rate * WINDOW_MS / 1000))
    hop = max(1, round(sample_rate * HOP_MS / 1000))
    if len(source) < width:
        return np.empty(0, dtype=np.float64), np.empty(0, dtype=np.int64)
    squared = np.square(source, dtype=np.float64)
    prefix = np.empty(len(squared) + 1, dtype=np.float64)
    prefix[0] = 0.0
    np.cumsum(squared, dtype=np.float64, out=prefix[1:])
    starts = np.arange(0, len(source) - width + 1, hop, dtype=np.int64)
    values = np.sqrt((prefix[starts + width] - prefix[starts]) / width)
    return values, starts


def detect_audio_tones(bandpassed_samples, spectral_samples, sample_rate=SAMPLE_RATE_HZ):
    """Detect candidate bursts, then apply the fixed 60 ms envelope and spectral classifier."""
    rms, starts = window_rms(bandpassed_samples, sample_rate)
    if rms.size == 0:
        return {"rms": rms, "window_starts": starts, "statistics": {}, "candidates": [], "tone_events": []}

    median = float(np.median(rms))
    p99 = float(np.percentile(rms, RMS_PERCENTILE))
    maximum = float(np.max(rms))
    threshold = median + max(MINIMUM_RMS_RISE, (p99 - median) * THRESHOLD_FRACTION)
    active = np.flatnonzero(rms > threshold)
    groups = []
    for index in active:
        if not groups or index - groups[-1][-1] > MAX_INTER_WINDOW_GAP:
            groups.append([int(index)])
        else:
            groups[-1].append(int(index))

    candidates = []
    tone_events = []
    spectral = np.asarray(spectral_samples, dtype=np.float32)
    for group in groups:
        if len(group) < MIN_CANDIDATE_WINDOWS:
            continue
        start_sample = int(starts[group[0]])
        # Match v2's candidate duration: span between first and last active window starts.
        duration_samples = max(0, int(starts[group[-1]]) - start_sample)
        duration_ms = duration_samples * 1000.0 / sample_rate
        stable_window = locate_stable_envelope_window(spectral, start_sample, duration_samples, sample_rate)
        if stable_window is None:
            sample_start = None
            concentration = 0.0
        else:
            sample_start = stable_window["start_sample"]
            tone_samples = spectral[sample_start:sample_start + stable_window["window_samples"]]
            concentration = target_power_fraction(tone_samples, sample_rate)
        candidate = {
            "time_seconds": start_sample / sample_rate,
            "duration_ms": duration_ms,
            "classification_window_start_seconds": sample_start / sample_rate if sample_start is not None else None,
            "classification_window_offset_ms": stable_window["offset_ms"] if stable_window is not None else None,
            "classification_window_duration_ms": stable_window["window_ms"] if stable_window is not None else None,
            "envelope_baseline_rms": stable_window["baseline_rms"] if stable_window is not None else None,
            "envelope_p90_rms": stable_window["p90_active_rms"] if stable_window is not None else None,
            "envelope_threshold_rms": stable_window["threshold_rms"] if stable_window is not None else None,
            "envelope_tile_rms": stable_window["tile_rms"] if stable_window is not None else [],
            "window_policy": STABLE_ENVELOPE_POLICY_VERSION,
            "window_selection": "earliest fixed 60 ms window with all six successive 10 ms PCM RMS tiles at or above baseline + 0.5 * (candidate p90 RMS - baseline); 5 ms search step",
            "target_band_hz": list(TARGET_BAND_HZ),
            "comparison_band_hz": list(COMPARISON_BAND_HZ),
            "target_power_fraction": concentration,
            "accepted_as_1khz_fixture_tone": concentration >= MIN_TARGET_POWER_FRACTION,
        }
        candidates.append(candidate)
        if candidate["accepted_as_1khz_fixture_tone"]:
            tone_events.append(candidate["time_seconds"])

    return {
        "rms": rms,
        "window_starts": starts,
        "statistics": {
            "median_rms": median,
            "p99_rms": p99,
            "maximum_rms": maximum,
            "actual_threshold_rms": threshold,
            "candidate_gate_policy": POLICY_VERSION,
            "rms_percentile": RMS_PERCENTILE,
            "minimum_rms_rise": MINIMUM_RMS_RISE,
            "threshold_fraction": THRESHOLD_FRACTION,
            "window_ms": WINDOW_MS,
            "hop_ms": HOP_MS,
        },
        "candidates": candidates,
        "tone_events": tone_events,
    }
