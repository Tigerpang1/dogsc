"""Append an estimated fixture-median-bias correction to analyze-ffplay-sync outputs."""
import csv
import json
import pathlib
import sys

import numpy as np


def read_rows(path):
    with pathlib.Path(path).open(encoding="utf-8-sig", newline="") as source:
        return list(csv.DictReader(source))


def stats(values):
    ordered = sorted(abs(value) for value in values)
    if not ordered:
        return {"count": 0, "median_ms": None, "p95_abs_ms": None, "p99_abs_ms": None, "max_abs_ms": None}
    absolute = np.abs(np.asarray(values, dtype=np.float64))
    return {"count": len(values), "median_ms": float(np.median(values)), "p95_abs_ms": float(np.percentile(absolute, 95)),
            "p99_abs_ms": float(np.percentile(absolute, 99)), "max_abs_ms": float(np.max(absolute))}


def main():
    if len(sys.argv) < 3:
        raise SystemExit("Usage: annotate-fixture-bias.py <fixture-analysis.csv> <capture-analysis.csv> [capture-analysis.csv ...]")
    fixture_rows = read_rows(sys.argv[1])
    fixture = [float(row["audio_minus_video_ms"]) for row in fixture_rows if row.get("audio_minus_video_ms")]
    if not fixture:
        raise SystemExit("Fixture analysis contains no matched A-V events.")
    fixture_bias = float(np.median(np.asarray(fixture, dtype=np.float64)))
    reports = []
    for filename in sys.argv[2:]:
        path = pathlib.Path(filename)
        rows = read_rows(path)
        raw_values = []
        corrected_values = []
        for row in rows:
            raw = row.get("audio_minus_video_ms", "")
            if raw:
                value = float(raw)
                adjusted = value - fixture_bias
                row["fixture_intrinsic_audio_minus_video_ms"] = f"{fixture_bias:.3f}"
                row["capture_audio_minus_video_after_fixture_median_bias_estimate_ms"] = f"{adjusted:.3f}"
                raw_values.append(value)
                corrected_values.append(adjusted)
            else:
                row["fixture_intrinsic_audio_minus_video_ms"] = f"{fixture_bias:.3f}"
                row["capture_audio_minus_video_after_fixture_median_bias_estimate_ms"] = ""
        fields = list(rows[0]) if rows else ["video_flash_seconds", "audio_tone_seconds", "audio_minus_video_ms",
                                               "fixture_intrinsic_audio_minus_video_ms", "capture_audio_minus_video_after_fixture_median_bias_estimate_ms"]
        with path.open("w", encoding="utf-8", newline="") as output:
            writer = csv.DictWriter(output, fieldnames=fields)
            writer.writeheader()
            writer.writerows(rows)
        reports.append({"analysis_csv": str(path), "raw": stats(raw_values), "fixture_bias_corrected": stats(corrected_values)})
    print(json.dumps({"fixture_analysis": sys.argv[1], "fixture_event_count": len(fixture),
                      "fixture_median_bias_estimate_ms": fixture_bias,
                      "correction_note": "Estimate uses the median of the measured fixture cue biases; it is not per-cue calibration or proof of absolute physical AV accuracy.",
                      "captures": reports}, indent=2))


if __name__ == "__main__":
    main()
