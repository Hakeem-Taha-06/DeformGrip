"""Influence-matrix binary exporter for Role 2b.

Builds the surface influence matrix G block-by-block using Level.influence_blocks()
(without holding the full float64 matrix in memory) and writes:
    solver/export/<level>/G.bin        float32 little-endian, column-major
    solver/export/<level>/G.meta.json  metadata per CONTRACT 6.5

Usage:
    python solver/export.py [--levels coarse medium fine] [--block 256] [--skip-verify]
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cases import (REPO, amplitude_tag, case_ids, level_names, load_case,  # noqa: E402
                   load_settings)
from fem_core import Level  # noqa: E402

LAYOUT_DESC = "float32 LE, column-major, index = col*3*n_surface + row"


def load_G_bin(g_bin_path: Path, n_surface: int) -> np.ndarray:
    """Read G.bin back as a (3*n_surface, 3*n_surface) float32 array in column-major order."""
    n_dof = 3 * n_surface
    expected_bytes = 4 * n_dof * n_dof
    actual_bytes = g_bin_path.stat().st_size
    if actual_bytes != expected_bytes:
        raise RuntimeError(
            f"{g_bin_path}: size {actual_bytes} bytes != expected {expected_bytes} bytes"
        )
    raw = np.fromfile(g_bin_path, dtype="<f4")
    return raw.reshape((n_dof, n_dof), order="F")


def export_level(level_name: str, settings: dict, out_root: Path, block: int = 256) -> dict:
    """Export G.bin and G.meta.json for one mesh level and return summary metrics."""
    out_dir = out_root / level_name
    out_dir.mkdir(parents=True, exist_ok=True)
    g_bin_path = out_dir / "G.bin"
    g_meta_path = out_dir / "G.meta.json"

    t_start = time.perf_counter()
    level = Level(level_name, settings)
    n_dof = 3 * level.ns
    expected_bytes = 4 * n_dof * n_dof

    t_build_start = time.perf_counter()
    with open(g_bin_path, "wb") as fh:
        for _, blk in level.influence_blocks(block=block):
            # blk has shape (n_dof, nb); transposing to (nb, n_dof) C-contiguous
            # writes each column contiguously in little-endian float32.
            np.ascontiguousarray(blk.T, dtype="<f4").tofile(fh)
    t_build = time.perf_counter() - t_build_start
    t_total = time.perf_counter() - t_start

    actual_bytes = g_bin_path.stat().st_size
    if actual_bytes != expected_bytes:
        raise RuntimeError(
            f"{level_name}: wrote {actual_bytes} bytes, expected {expected_bytes}"
        )

    mat = settings["material"]
    meta = {
        "mesh_level": level_name,
        "n_surface": int(level.ns),
        "units": "m per N",
        "E0_Pa": float(level.E0),
        "nu": float(level.nu),
        "E_inf_Pa": float(mat["E_inf_Pa"]),
        "E1_Pa": float(mat["E1_Pa"]),
        "tau1_s": float(mat["tau1_s"]),
        "layout": LAYOUT_DESC,
    }
    g_meta_path.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")

    return {
        "level": level_name,
        "n_surface": level.ns,
        "n_dof": n_dof,
        "bytes": actual_bytes,
        "factor_s": level.t_assemble + level.t_factor,
        "build_s": t_build,
        "total_s": t_total,
        "level_obj": level,
        "g_bin_path": g_bin_path,
    }


def verify_level_export(info: dict, settings: dict, static_dir: Path, seed: int = 0) -> None:
    """Verify G.bin against direct Level columns and saved 10 N static results."""
    level: Level = info["level_obj"]
    lvl = info["level"]
    G = load_G_bin(info["g_bin_path"], level.ns)

    rng = np.random.default_rng(seed)
    n_check = min(32, 3 * level.ns)
    cols = np.sort(rng.choice(3 * level.ns, size=n_check, replace=False))
    ref_cols = level.G_columns(cols)
    got_cols = G[:, cols].astype(np.float64)
    col_rel = float(np.linalg.norm(got_cols - ref_cols) / max(np.linalg.norm(ref_cols), 1e-30))
    if col_rel >= 1e-5:
        raise RuntimeError(f"{lvl}: sampled G.bin columns rel error {col_rel:.2e} >= 1e-5")

    amp = 10
    tag = amplitude_tag(amp)
    worst_static_rel = 0.0
    checked_cases = 0
    for cid in case_ids(settings):
        static_path = static_dir / f"{lvl}_{cid}_{tag}.npy"
        if not static_path.exists():
            continue
        case = load_case(lvl, cid, level.ns)
        p = case.pattern()
        nz = np.flatnonzero(p)
        u_export = (float(amp) * (G[:, nz].astype(np.float64) @ p[nz])).reshape(level.ns, 3)
        u_static = np.load(static_path, allow_pickle=False)
        rel = float(np.linalg.norm(u_export - u_static) / np.linalg.norm(u_static))
        worst_static_rel = max(worst_static_rel, rel)
        checked_cases += 1
        if rel >= 1e-5:
            raise RuntimeError(f"{lvl}/{cid}: 10*(G@p) vs {static_path.name} rel error {rel:.2e} >= 1e-5")

    print(
        f"  VERIFY {lvl}: col_rel={col_rel:.2e}, "
        f"static_10N_max_rel={worst_static_rel:.2e} ({checked_cases} cases)"
    )


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--levels", nargs="*", help="Mesh levels to export (default: all in settings)")
    ap.add_argument("--out-dir", type=Path, default=REPO / "solver" / "export")
    ap.add_argument("--static-dir", type=Path, default=REPO / "results" / "ours" / "static")
    ap.add_argument("--block", type=int, default=256, help="Column block size for influence_blocks()")
    ap.add_argument("--skip-verify", action="store_true")
    args = ap.parse_args()

    settings = load_settings()
    levels = args.levels or level_names(settings)

    print(f"Exporting G.bin + G.meta.json to {args.out_dir} ...")
    for lvl in levels:
        info = export_level(lvl, settings, args.out_dir, block=args.block)
        mb = info["bytes"] / (1024 * 1024)
        print(
            f"[{lvl}] n_surface={info['n_surface']} (n_dof={info['n_dof']}) | "
            f"G.bin={info['bytes']:,} bytes ({mb:.2f} MiB) | "
            f"factor={info['factor_s']:.2f}s, build={info['build_s']:.2f}s, total={info['total_s']:.2f}s"
        )
        if not args.skip_verify:
            verify_level_export(info, settings, args.static_dir)

    return 0


if __name__ == "__main__":
    sys.exit(main())
