"""Timing benchmark for Role 2b.

Measures three separate quantities for each level x timing_case:
  (i)   assemble + factorize + solve from scratch
  (ii)  one back-substitution with the factorization cached
  (iii) precomputed runtime step in NumPy (G applied to nonzero load components)

Writes results/ours/timing/<level>_<case>.json per CONTRACT 6.6.

Usage:
    python solver/benchmark.py [--levels coarse medium fine] [--out-dir DIR]
"""
from __future__ import annotations

import argparse
import json
import platform
import sys
import time
from pathlib import Path

import numpy as np
import scipy
import skfem
from scipy.sparse.linalg import splu
from skfem import asm
from skfem.models.elasticity import lame_parameters, linear_elasticity

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cases import REPO, level_names, load_case, load_settings  # noqa: E402
from export import load_G_bin  # noqa: E402
from fem_core import Level  # noqa: E402


def _stats_ms(samples_s: list[float], what_is_timed: str) -> dict:
    arr = np.asarray(samples_s, dtype=np.float64) * 1000.0
    return {
        "what_is_timed": what_is_timed,
        "n_samples": int(arr.size),
        "ms_mean": round(float(arr.mean()), 4),
        "ms_p95": round(float(np.percentile(arr, 95)), 4),
        "ms_max": round(float(arr.max()), 4),
    }


def _hardware_summary() -> str:
    cpu = platform.processor().strip() or platform.machine()
    import os
    cores = os.cpu_count() or 1
    return f"{cpu} ({cores} logical CPUs), {platform.system()} {platform.release()}"


def _software_summary() -> str:
    return (
        f"Python {platform.python_version()}, "
        f"scikit-fem {skfem.__version__}, "
        f"scipy {scipy.__version__}, "
        f"numpy {np.__version__}"
    )


def measure_precompute_s(level: Level, block: int = 256) -> float:
    """Measure one-off precompute time: assemble + factorize + build all columns of G."""
    t0 = time.perf_counter()
    for _ in level.influence_blocks(block=block):
        pass
    t_build = time.perf_counter() - t0
    return float(level.t_assemble + level.t_factor + t_build)


def bench_from_scratch(level: Level, rhs_full: np.ndarray, repeats: int) -> list[float]:
    """(i) Assemble K + slice free DOFs + splu factorize + solve."""
    lame = lame_parameters(level.E0, level.nu)
    free = level.free
    rhs_free = rhs_full[free]
    surf_dofs = level.surf_dofs
    ns = level.ns
    samples: list[float] = []
    for _ in range(repeats):
        t0 = time.perf_counter()
        K = asm(linear_elasticity(*lame), level.basis).tocsc()
        lu = splu(K[free][:, free].tocsc())
        u = np.zeros(level.N)
        u[free] = lu.solve(rhs_free)
        _ = u[surf_dofs].reshape(ns, 3)
        samples.append(time.perf_counter() - t0)
    return samples


def bench_backsub(level: Level, F_surf: np.ndarray, repeats: int) -> list[float]:
    """(ii) One back-substitution with factorization cached."""
    # Warmup
    _ = level.solve_loads(F_surf)
    samples: list[float] = []
    for _ in range(repeats):
        t0 = time.perf_counter()
        _ = level.solve_loads(F_surf)
        samples.append(time.perf_counter() - t0)
    return samples


def bench_G_apply(G_f32: np.ndarray, F_surf: np.ndarray, n_surface: int, repeats: int) -> list[float]:
    """(iii) Precomputed runtime step in NumPy: G applied to nonzero load components."""
    nz = np.flatnonzero(F_surf)
    f_nz = F_surf[nz].astype(np.float32)
    # Warmup
    _ = (G_f32[:, nz] @ f_nz).reshape(n_surface, 3)
    samples: list[float] = []
    for _ in range(repeats):
        t0 = time.perf_counter()
        _ = (G_f32[:, nz] @ f_nz).reshape(n_surface, 3)
        samples.append(time.perf_counter() - t0)
    return samples


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--levels", nargs="*", help="Mesh levels to benchmark (default: all in settings)")
    ap.add_argument("--export-dir", type=Path, default=REPO / "solver" / "export")
    ap.add_argument("--out-dir", type=Path, default=REPO / "results" / "ours" / "timing")
    ap.add_argument("--amp-N", type=float, default=10.0, help="Force amplitude used for load vector")
    args = ap.parse_args()

    settings = load_settings()
    levels = args.levels or level_names(settings)
    timing_cases = list(settings["timing_cases"])
    args.out_dir.mkdir(parents=True, exist_ok=True)

    hw = _hardware_summary()
    sw = _software_summary()
    print(f"Benchmarking levels={levels}, cases={timing_cases}")
    print(f"  Hardware: {hw}")
    print(f"  Software: {sw}")

    for lvl in levels:
        level = Level(lvl, settings)
        g_bin_path = args.export_dir / lvl / "G.bin"
        if not g_bin_path.exists():
            raise FileNotFoundError(
                f"Missing {g_bin_path}; run `python solver/export.py` before benchmarking."
            )
        export_bytes = int(g_bin_path.stat().st_size)
        G_f32 = load_G_bin(g_bin_path, level.ns)
        precompute_s = measure_precompute_s(level)

        scratch_repeats = 2 if (level.t_assemble + level.t_factor) > 2.0 else 3
        backsub_repeats = 50 if level.ns > 2000 else 100
        g_repeats = 200

        for cid in timing_cases:
            case = load_case(lvl, cid, level.ns)
            F_surf = args.amp_N * case.pattern()
            rhs_full = np.zeros(level.N)
            rhs_full[level.surf_dofs] = F_surf
            nz_count = int(np.count_nonzero(F_surf))

            t_scratch = bench_from_scratch(level, rhs_full, repeats=scratch_repeats)
            t_backsub = bench_backsub(level, F_surf, repeats=backsub_repeats)
            t_g_apply = bench_G_apply(G_f32, F_surf, level.ns, repeats=g_repeats)

            m_scratch = _stats_ms(
                t_scratch,
                "Python/scikit-fem full solve from scratch (assemble K + slice free DOFs + splu factorize + solve)",
            )
            m_backsub = _stats_ms(
                t_backsub,
                "Python/scipy cached back-substitution (Level.solve_loads with cached splu factorization)",
            )
            m_g = _stats_ms(
                t_g_apply,
                f"NumPy precomputed G apply on {nz_count} nonzero load components (float32 column-major G[:, nz] @ f_nz)",
            )

            report = {
                "hardware": hw,
                "software": sw,
                "mesh_level": lvl,
                "case_id": cid,
                "n_surface": int(level.ns),
                "n_nonzero_load_components": nz_count,
                "precompute_s": round(precompute_s, 4),
                "export_bytes": export_bytes,
                "measurements": [m_scratch, m_backsub, m_g],
            }

            out_path = args.out_dir / f"{lvl}_{cid}.json"
            out_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            print(
                f"  [{lvl}/{cid}] scratch={m_scratch['ms_mean']:.2f} ms | "
                f"backsub={m_backsub['ms_mean']:.3f} ms | "
                f"G_apply={m_g['ms_mean']:.4f} ms | "
                f"precompute={precompute_s:.2f}s -> {out_path.name}"
            )

    return 0


if __name__ == "__main__":
    sys.exit(main())
