#!/usr/bin/env python3
"""
Role 4: Generate case JSON files from config/test_settings.json and model/<level>/surface.json.

For each level in mesh_levels and each case in cases.list:
  - Load model/<level>/surface.json.
  - type "cap": centre direction c = (sin(pol)cos(az), sin(pol)sin(az), cos(pol)),
    pol measured from +z, az from +x toward +y. Select rows with angle(d, c) <= half_angle_deg.
    We use dot(d, c) >= cos(half_angle) (equivalent to angle <= half_angle, avoids arccos
    boundary drift). Weights = areas of selected rows, normalized to sum 1.
    Direction: "inward_normal" -> per-row -d; a 3-vector -> normalized once, same for all rows.
    Force of row i = w_i * direction_i.
  - type "random": rng = np.random.default_rng(seed). Pick n_nodes DISTINCT rows from
    range(n_surface) with rng.choice(replace=False); random unit 3D direction (normalized
    Gaussian) and random positive weight (rng.uniform(0.1, 1.0)). Generate all random numbers
    first, THEN sort rows ascending keeping each row paired with its direction and weight.
  - Normalization (CONTRACT 4.2): divide by sum of Euclidean norms so sum_i ||f_i|| = 1.
    Assert |sum - 1| <= 1e-12.
  - Output config/cases/<level>/<case_id>.json with fields: case_id, mesh_level, n_surface,
    physical (true for cap, false for random), spec (copy of settings entry),
    loads = [{"row": int, "f": [fx, fy, fz]} ...] rows distinct and ascending.

Assertions on every case: len(loads) >= cases.min_nodes; rows distinct, ascending,
within [0, n_surface); sum of norms == 1 within 1e-12.
If a cap has fewer than min_nodes nodes: STOP, print case/level/count and the smallest
half_angle (1-degree steps) reaching min_nodes, then wait.

Informational: print per level and case the number of loaded nodes and how many are in
fixed_nodes (loads on fixed nodes produce no displacement by contract; expected for
pressure_all and possibly random cases).

--check: re-read all 30 files and re-verify schema + assertions, print pass/fail table
(rows = cases, columns = levels, cell = n loaded nodes).

Usage:
    python compare/make_cases.py
    python compare/make_cases.py --check
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np

# Resolve repo root as the directory two levels up from this file
# (compare/make_cases.py -> compare/ -> repo root).
REPO_ROOT = Path(__file__).resolve().parents[1]
SETTINGS_PATH = REPO_ROOT / "config" / "test_settings.json"
MODEL_DIR = REPO_ROOT / "model"
CASES_DIR = REPO_ROOT / "config" / "cases"


def load_settings() -> dict:
    with SETTINGS_PATH.open("r", encoding="utf-8") as fh:
        return json.load(fh)


def load_surface(level: str) -> dict:
    path = MODEL_DIR / level / "surface.json"
    with path.open("r", encoding="utf-8") as fh:
        return json.load(fh)


def cap_centre_direction(polar_deg: float, azimuth_deg: float) -> np.ndarray:
    """Centre direction on the unit sphere: pol from +z, az from +x toward +y."""
    pol = math.radians(polar_deg)
    az = math.radians(azimuth_deg)
    return np.array(
        [math.sin(pol) * math.cos(az), math.sin(pol) * math.sin(az), math.cos(pol)],
        dtype=np.float64,
    )


def build_cap_case(
    case_id: str, spec: dict, surface: dict, min_nodes: int
) -> tuple[list[dict], int, int]:
    """Build a 'cap' case. Returns (loads, n_loaded, n_fixed_loaded)."""
    positions = np.asarray(surface["surface_positions"], dtype=np.float64)
    areas = np.asarray(surface["surface_areas"], dtype=np.float64)
    surface_nodes = surface["surface_nodes"]
    fixed_nodes = set(surface.get("fixed_nodes", []))
    n_surface = len(surface_nodes)

    # Unit direction of each surface row: d = x / |x|
    norms = np.linalg.norm(positions, axis=1)
    d = positions / norms[:, None]

    c = cap_centre_direction(float(spec["polar_deg"]), float(spec["azimuth_deg"]))
    half_angle = math.radians(float(spec["half_angle_deg"]))
    cos_half = math.cos(half_angle)

    # Select rows with angle(d, c) <= half_angle  <=>  dot(d, c) >= cos(half_angle)
    dots = d @ c
    selected_mask = dots >= cos_half - 1e-15  # small epsilon for boundary safety
    selected_rows = [int(surface_nodes[i]) for i in np.nonzero(selected_mask)[0]]

    if len(selected_rows) == 0:
        raise RuntimeError(
            f"Case '{case_id}': cap selected zero surface nodes "
            f"(polar={spec['polar_deg']}, azimuth={spec['azimuth_deg']}, "
            f"half_angle={spec['half_angle_deg']})."
        )

    # Weights = areas of selected rows, normalized to sum 1
    sel_indices = np.nonzero(selected_mask)[0]
    sel_areas = areas[sel_indices]
    weights = sel_areas / sel_areas.sum()

    # Direction per row
    direction_spec = spec["direction"]
    if direction_spec == "inward_normal":
        dirs = -d[sel_indices]  # per-row inward normal
    else:
        # Global 3-vector: normalize once, same for all rows
        gv = np.asarray(direction_spec, dtype=np.float64)
        gv = gv / np.linalg.norm(gv)
        dirs = np.tile(gv, (len(sel_indices), 1))

    forces = weights[:, None] * dirs  # (n_selected, 3)

    # Normalization: sum of Euclidean norms == 1
    norm_sum = float(np.sum(np.linalg.norm(forces, axis=1)))
    if norm_sum <= 0.0:
        raise RuntimeError(f"Case '{case_id}': sum of force norms is zero.")
    forces = forces / norm_sum

    loads = [
        {"row": int(row), "f": [float(fx), float(fy), float(fz)]}
        for row, (fx, fy, fz) in zip(selected_rows, forces)
    ]
    loads.sort(key=lambda ld: ld["row"])

    n_loaded = len(loads)
    n_fixed_loaded = sum(1 for ld in loads if ld["row"] in fixed_nodes)

    return loads, n_loaded, n_fixed_loaded


def build_random_case(
    case_id: str, spec: dict, surface: dict, min_nodes: int
) -> tuple[list[dict], int, int]:
    """Build a 'random' case. Returns (loads, n_loaded, n_fixed_loaded)."""
    surface_nodes = surface["surface_nodes"]
    fixed_nodes = set(surface.get("fixed_nodes", []))
    n_surface = len(surface_nodes)

    seed = int(spec["seed"])
    n_nodes = int(spec["n_nodes"])
    if n_nodes > n_surface:
        raise RuntimeError(
            f"Case '{case_id}': n_nodes={n_nodes} exceeds n_surface={n_surface}."
        )

    rng = np.random.default_rng(seed)

    # Pick n_nodes DISTINCT rows
    chosen_rows = rng.choice(n_surface, size=n_nodes, replace=False)

    # Generate ALL random numbers first: directions (Gaussian) and weights (uniform)
    gaussian = rng.standard_normal((n_nodes, 3))
    dirs = gaussian / np.linalg.norm(gaussian, axis=1)[:, None]
    weights = rng.uniform(0.1, 1.0, size=n_nodes)

    # Sort rows ascending, keeping each row paired with its direction and weight
    order = np.argsort(chosen_rows)
    chosen_rows = chosen_rows[order]
    dirs = dirs[order]
    weights = weights[order]

    forces = weights[:, None] * dirs

    # Normalization: sum of Euclidean norms == 1
    norm_sum = float(np.sum(np.linalg.norm(forces, axis=1)))
    if norm_sum <= 0.0:
        raise RuntimeError(f"Case '{case_id}': sum of force norms is zero.")
    forces = forces / norm_sum

    loads = [
        {"row": int(surface_nodes[int(r)]), "f": [float(fx), float(fy), float(fz)]}
        for r, (fx, fy, fz) in zip(chosen_rows, forces)
    ]
    loads.sort(key=lambda ld: ld["row"])

    n_loaded = len(loads)
    n_fixed_loaded = sum(1 for ld in loads if ld["row"] in fixed_nodes)

    return loads, n_loaded, n_fixed_loaded


def smallest_half_angle_for_min_nodes(
    case_id: str, spec: dict, surface: dict, min_nodes: int, max_angle: int = 180
) -> int:
    """Find the smallest half_angle (1-degree steps) giving >= min_nodes nodes."""
    positions = np.asarray(surface["surface_positions"], dtype=np.float64)
    norms = np.linalg.norm(positions, axis=1)
    d = positions / norms[:, None]
    c = cap_centre_direction(float(spec["polar_deg"]), float(spec["azimuth_deg"]))
    dots = d @ c

    for angle in range(1, max_angle + 1):
        cos_half = math.cos(math.radians(angle))
        count = int(np.sum(dots >= cos_half - 1e-15))
        if count >= min_nodes:
            return angle
    return max_angle


def validate_case(
    case_id: str, level: str, loads: list[dict], n_surface: int, min_nodes: int
) -> None:
    """Run all assertions on a built case. Raises on failure."""
    rows = [ld["row"] for ld in loads]

    # Distinct
    if len(set(rows)) != len(rows):
        raise ValueError(f"Case '{case_id}' ({level}): duplicate rows in loads.")

    # Ascending
    if rows != sorted(rows):
        raise ValueError(f"Case '{case_id}' ({level}): rows not ascending.")

    # Within range
    for r in rows:
        if r < 0 or r >= n_surface:
            raise ValueError(
                f"Case '{case_id}' ({level}): row {r} outside [0, {n_surface})."
            )

    # min_nodes
    if len(loads) < min_nodes:
        raise ValueError(
            f"Case '{case_id}' ({level}): {len(loads)} loaded nodes < min_nodes={min_nodes}."
        )

    # Sum of norms == 1 within 1e-12
    norm_sum = float(np.sum(np.linalg.norm([ld["f"] for ld in loads], axis=1)))
    if abs(norm_sum - 1.0) > 1e-12:
        raise ValueError(
            f"Case '{case_id}' ({level}): sum of norms = {norm_sum}, not 1 within 1e-12."
        )


def write_case_file(
    case_id: str, level: str, n_surface: int, physical: bool, spec: dict, loads: list[dict]
) -> None:
    level_dir = CASES_DIR / level
    level_dir.mkdir(parents=True, exist_ok=True)
    out_path = level_dir / f"{case_id}.json"

    case_obj = {
        "case_id": case_id,
        "mesh_level": level,
        "n_surface": n_surface,
        "physical": physical,
        "spec": dict(spec),
        "loads": loads,
    }

    with out_path.open("w", encoding="utf-8", newline="\n") as fh:
        json.dump(case_obj, fh, indent=2)
        fh.write("\n")

    print(f"  Wrote {out_path} ({len(loads)} loads)")


def generate_all(settings: dict) -> dict:
    """Generate all case files. Returns info dict for reporting."""
    mesh_levels = settings["mesh_levels"]
    cases_list = settings["cases"]["list"]
    min_nodes = int(settings["cases"]["min_nodes"])

    info: dict[str, dict[str, dict]] = {}  # level -> case_id -> {n_loaded, n_fixed_loaded}

    for level in mesh_levels:
        surface = load_surface(level)
        n_surface = int(surface["n_surface"])
        info[level] = {}

        for case_id, spec in cases_list.items():
            ctype = spec["type"]

            if ctype == "cap":
                loads, n_loaded, n_fixed_loaded = build_cap_case(
                    case_id, spec, surface, min_nodes
                )
                physical = True
            elif ctype == "random":
                loads, n_loaded, n_fixed_loaded = build_random_case(
                    case_id, spec, surface, min_nodes
                )
                physical = False
            else:
                raise ValueError(f"Unknown case type '{ctype}' for case '{case_id}'.")

            # Check min_nodes before writing; if not met, stop and report
            if n_loaded < min_nodes:
                needed_angle = smallest_half_angle_for_min_nodes(
                    case_id, spec, surface, min_nodes
                )
                print(
                    f"\nERROR: Case '{case_id}' on level '{level}' has only "
                    f"{n_loaded} surface nodes in the cap, fewer than min_nodes={min_nodes}.\n"
                    f"Smallest half_angle (1-degree steps) reaching >= {min_nodes} nodes: "
                    f"{needed_angle} degrees.\n"
                    f"Do not enlarge silently - waiting for your decision.\n"
                )
                sys.exit(1)

            validate_case(case_id, level, loads, n_surface, min_nodes)
            write_case_file(case_id, level, n_surface, physical, spec, loads)

            info[level][case_id] = {
                "n_loaded": n_loaded,
                "n_fixed_loaded": n_fixed_loaded,
            }

    return info


def print_info_table(info: dict, settings: dict) -> None:
    """Informational table: per level and case, n loaded nodes and n in fixed_nodes."""
    mesh_levels = settings["mesh_levels"]
    cases_list = settings["cases"]["list"]
    min_nodes = int(settings["cases"]["min_nodes"])

    print("\n=== Informational: loaded nodes per case (and how many are fixed) ===")
    header = f"{'case_id':<16} | " + " | ".join(f"{lvl:>8}" for lvl in mesh_levels)
    print(header)
    print("-" * len(header))
    for case_id in cases_list:
        row = f"{case_id:<16} |"
        for lvl in mesh_levels:
            entry = info.get(lvl, {}).get(case_id, None)
            if entry is None:
                row += f"  {'N/A':>6} |"
            else:
                row += f" {entry['n_loaded']:>4}/{entry['n_fixed_loaded']:<2} |"
        print(row)
    print("(format: n_loaded/n_fixed_loaded)\n")


def check_all(settings: dict) -> bool:
    """Re-read all 30 case files and verify schema + assertions."""
    mesh_levels = settings["mesh_levels"]
    cases_list = settings["cases"]["list"]
    min_nodes = int(settings["cases"]["min_nodes"])

    all_ok = True

    print("\n=== make_cases.py --check (re-verifying all case files) ===\n")

    # Per-file checks
    for level in mesh_levels:
        surface = load_surface(level)
        n_surface = int(surface["n_surface"])
        fixed_nodes = set(surface.get("fixed_nodes", []))

        for case_id, spec in cases_list.items():
            path = CASES_DIR / level / f"{case_id}.json"
            results: list[tuple[str, bool, str]] = []

            if not path.exists():
                results.append(("file exists", False, f"missing {path}"))
                for name, ok, detail in results:
                    status = "PASS" if ok else "FAIL"
                    if not ok:
                        all_ok = False
                    print(f"  [{level}/{case_id}] {status:4} | {name:<30} | {detail}")
                continue

            with path.open("r", encoding="utf-8") as fh:
                case_obj = json.load(fh)

            # Schema keys
            required = {"case_id", "mesh_level", "n_surface", "physical", "spec", "loads"}
            has_keys = required.issubset(case_obj.keys())
            results.append(("schema keys", has_keys, str(sorted(case_obj.keys()))))

            # case_id / mesh_level match
            id_ok = case_obj.get("case_id") == case_id
            results.append(("case_id matches", id_ok, str(case_obj.get("case_id"))))
            lvl_ok = case_obj.get("mesh_level") == level
            results.append(("mesh_level matches", lvl_ok, str(case_obj.get("mesh_level"))))

            # n_surface matches surface.json
            ns_ok = case_obj.get("n_surface") == n_surface
            results.append(
                ("n_surface matches", ns_ok, f"{case_obj.get('n_surface')} vs {n_surface}")
            )

            # physical flag
            expected_physical = spec["type"] == "cap"
            phys_ok = case_obj.get("physical") == expected_physical
            results.append(
                ("physical flag", phys_ok, f"{case_obj.get('physical')} (expected {expected_physical})")
            )

            # spec matches settings entry
            spec_ok = case_obj.get("spec") == spec
            results.append(("spec matches settings", spec_ok, ""))

            loads = case_obj.get("loads", [])
            rows = [ld["row"] for ld in loads]

            # rows distinct
            distinct_ok = len(set(rows)) == len(rows)
            results.append(("rows distinct", distinct_ok, f"{len(rows)} loads"))

            # rows ascending
            asc_ok = rows == sorted(rows)
            results.append(("rows ascending", asc_ok, ""))

            # rows within range
            inrange_ok = all(0 <= r < n_surface for r in rows)
            results.append(("rows in [0, n_surface)", inrange_ok, ""))

            # min_nodes
            min_ok = len(loads) >= min_nodes
            results.append(
                ("min_nodes", min_ok, f"{len(loads)} >= {min_nodes}")
            )

            # sum of norms == 1 within 1e-12
            norm_sum = float(np.sum(np.linalg.norm([ld["f"] for ld in loads], axis=1)))
            norm_ok = abs(norm_sum - 1.0) <= 1e-12
            results.append(
                ("sum norms == 1", norm_ok, f"{norm_sum:.15f}")
            )

            for name, ok, detail in results:
                status = "PASS" if ok else "FAIL"
                if not ok:
                    all_ok = False
                print(f"  [{level}/{case_id}] {status:4} | {name:<30} | {detail}")

    # Summary table: rows = cases, columns = levels, cell = n loaded nodes
    print("\n=== Summary table: n loaded nodes per case per level ===")
    header = f"{'case_id':<16} | " + " | ".join(f"{lvl:>8}" for lvl in mesh_levels)
    print(header)
    print("-" * len(header))
    for case_id in cases_list:
        row = f"{case_id:<16} |"
        for lvl in mesh_levels:
            path = CASES_DIR / lvl / f"{case_id}.json"
            if not path.exists():
                row += f"  {'MISSING':>8} |"
                continue
            with path.open("r", encoding="utf-8") as fh:
                case_obj = json.load(fh)
            row += f" {len(case_obj.get('loads', [])):>8} |"
        print(row)

    print()
    print(f"OVERALL: {'ALL CHECKS PASSED' if all_ok else 'FAILED'}")
    return all_ok


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Role 4: Generate case JSON files from settings and surface meshes."
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Re-read all generated case files and verify schema + assertions.",
    )
    args = parser.parse_args()

    settings = load_settings()

    if args.check:
        ok = check_all(settings)
        return 0 if ok else 1

    info = generate_all(settings)
    print_info_table(info, settings)

    total_cases = sum(len(v) for v in info.values())
    print(f"\nGenerated {total_cases} case file(s) in {CASES_DIR}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
