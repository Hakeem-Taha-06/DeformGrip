#!/usr/bin/env python3
"""
Role 4: Generate force profile CSVs from config/test_settings.json.

Reads the `profiles` and `timing.dt_force_s` sections from the settings file.
Never hard-codes numbers.

Profile definition (CONTRACT 6.1):
    linear ramp 0 -> peak_N over ramp_s,
    hold peak for hold_s,
    linear release to 0 over release_s,
    then tail_s seconds at 0.

Output: config/profiles/<profile_id>.csv with header exactly "t,f_N",
one row every dt_force from t=0 to end of tail INCLUSIVE.
Uses integer sample indices (t = i*dt_force, n_rows = round(total/dt_force)+1)
to avoid float drift. Builds f with np.interp on breakpoints
[0, ramp, ramp+hold, ramp+hold+release, total].

Format: t with 3 decimals, f_N with 6 decimals.

Usage:
    python compare/make_profiles.py
    python compare/make_profiles.py --check
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

# Resolve repo root as the directory two levels up from this file
# (compare/make_profiles.py -> compare/ -> repo root).
REPO_ROOT = Path(__file__).resolve().parents[1]
SETTINGS_PATH = REPO_ROOT / "config" / "test_settings.json"
PROFILES_DIR = REPO_ROOT / "config" / "profiles"


def load_settings() -> dict:
    with SETTINGS_PATH.open("r", encoding="utf-8") as fh:
        return json.load(fh)


def profile_breakpoints(profile: dict) -> tuple[list[float], list[float]]:
    """Return (t_breakpoints, f_breakpoints) for the piecewise-linear profile."""
    ramp = float(profile["ramp_s"])
    hold = float(profile["hold_s"])
    release = float(profile["release_s"])
    tail = float(profile["tail_s"])
    peak = float(profile["peak_N"])

    t_pts = [0.0, ramp, ramp + hold, ramp + hold + release, ramp + hold + release + tail]
    f_pts = [0.0, peak, peak, 0.0, 0.0]
    return t_pts, f_pts


def generate_profile(profile_id: str, profile: dict, dt: float) -> tuple[np.ndarray, np.ndarray]:
    """Generate (t, f) arrays for one profile."""
    t_pts, f_pts = profile_breakpoints(profile)
    total = t_pts[-1]

    n_rows = int(round(total / dt)) + 1
    t = np.arange(n_rows, dtype=np.float64) * dt
    # Clip the last point to exactly total (avoid float drift beyond the tail).
    t = np.minimum(t, total)
    f = np.interp(t, t_pts, f_pts)
    return t, f


def write_csv(profile_id: str, profile: dict, dt: float) -> None:
    PROFILES_DIR.mkdir(parents=True, exist_ok=True)
    t, f = generate_profile(profile_id, profile, dt)
    out_path = PROFILES_DIR / f"{profile_id}.csv"
    with out_path.open("w", encoding="utf-8", newline="\n") as fh:
        fh.write("t,f_N\n")
        for ti, fi in zip(t, f):
            fh.write(f"{ti:.3f},{fi:.6f}\n")
    print(f"Wrote {out_path} ({t.size} rows, t in [0, {t[-1]:.3f}], f max = {f.max():.6f})")


def check_csv(profile_id: str, profile: dict, dt: float) -> list[tuple[str, bool, str]]:
    """Re-read the CSV and verify all assertions. Returns pass/fail rows."""
    results: list[tuple[str, bool, str]] = []
    out_path = PROFILES_DIR / f"{profile_id}.csv"

    peak = float(profile["peak_N"])
    total = (
        float(profile["ramp_s"])
        + float(profile["hold_s"])
        + float(profile["release_s"])
        + float(profile["tail_s"])
    )
    expected_rows = int(round(total / dt)) + 1

    if not out_path.exists():
        results.append((f"{profile_id}: file exists", False, f"missing {out_path}"))
        return results

    lines = out_path.read_text(encoding="utf-8").splitlines()

    # Header check
    header_ok = lines[0].strip() == "t,f_N"
    results.append(("header == t,f_N", header_ok, lines[0].strip() if lines else "(empty)"))

    # Parse rows
    t_vals: list[float] = []
    f_vals: list[float] = []
    parse_ok = True
    for line in lines[1:]:
        parts = line.strip().split(",")
        if len(parts) != 2:
            parse_ok = False
            break
        try:
            t_vals.append(float(parts[0]))
            f_vals.append(float(parts[1]))
        except ValueError:
            parse_ok = False
            break
    results.append(("rows parse as floats", parse_ok, f"{len(t_vals)} rows parsed"))

    if not parse_ok or not t_vals:
        return results

    t_arr = np.asarray(t_vals)
    f_arr = np.asarray(f_vals)

    # First row f == 0
    first_zero = abs(f_arr[0]) < 1e-12
    results.append(("first row f == 0", first_zero, f"f[0] = {f_arr[0]:.6f}"))

    # Max f == peak
    max_ok = abs(f_arr.max() - peak) < 1e-9
    results.append(("max f == peak_N", max_ok, f"max = {f_arr.max():.6f}, peak = {peak}"))

    # Last row f == 0
    last_zero = abs(f_arr[-1]) < 1e-12
    results.append(("last row f == 0", last_zero, f"f[-1] = {f_arr[-1]:.6f}"))

    # Duration matches ramp+hold+release+tail
    dur_ok = abs(t_arr[-1] - total) < 1e-9
    results.append(
        ("duration == ramp+hold+release+tail", dur_ok, f"t[-1] = {t_arr[-1]:.3f}, expected {total:.3f}")
    )

    # Constant step dt_force
    if t_arr.size > 1:
        steps = np.diff(t_arr)
        step_ok = bool(np.all(np.abs(steps - dt) < 1e-9))
        results.append(("constant step == dt_force", step_ok, f"dt = {dt}, steps min/max = {steps.min():.6f}/{steps.max():.6f}"))
    else:
        step_ok = True
        results.append(("constant step == dt_force", True, "single row"))

    # Row count
    count_ok = t_arr.size == expected_rows
    results.append(
        ("row count", count_ok, f"actual = {t_arr.size}, expected = {expected_rows}")
    )

    return results


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Role 4: Generate force profile CSVs from config/test_settings.json."
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Re-read generated CSVs and verify assertions, print pass/fail table.",
    )
    args = parser.parse_args()

    settings = load_settings()
    profiles = settings["profiles"]
    dt = float(settings["timing"]["dt_force_s"])

    if args.check:
        print(f"\n=== make_profiles.py --check (dt_force = {dt} s) ===\n")
        all_ok = True
        for pid, prof in profiles.items():
            results = check_csv(pid, prof, dt)
            print(f"--- {pid} ---")
            for name, ok, detail in results:
                status = "PASS" if ok else "FAIL"
                if not ok:
                    all_ok = False
                print(f"  {status:4} | {name:<35} | {detail}")
        print()
        print(f"OVERALL: {'ALL CHECKS PASSED' if all_ok else 'FAILED'}")
        return 0 if all_ok else 1

    for pid, prof in profiles.items():
        write_csv(pid, prof, dt)

    print(f"\nGenerated {len(profiles)} profile CSV(s) in {PROFILES_DIR}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
