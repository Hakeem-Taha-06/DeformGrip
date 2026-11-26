"""Element volume check of the linear solution (Role 2a).

The model is linear: the displacement field of a case scales with the force and the
equilibrium is written on the undeformed shape. This script applies the (volume) displacement
field to the mesh and reports how the volume of every tetrahedron changes:
    ratio = signed volume of the displaced tetrahedron / volume at rest.
ratio = 1 is no volume change; the minimum over all tetrahedra measures the largest local
compression (it can depend strongly on the mesh, so the 1st percentile and the fraction of
tetrahedra below 0.9 are reported too); ratio <= 0 means the tetrahedron is inverted, which
cannot be physical.
This is a geometric consistency indicator of the linear solution, not an error estimate.

Writes to results/ours/analysis/: volume_ratio.csv and fig5_volume_ratio.png.
Usage: python solver/volume_check.py [--levels coarse medium fine] [--out-dir DIR] [--static-dir DIR]
Forces: static.forces_N together with static.nonlinear_check_forces_N (forces above the static
list are an extrapolation of the linear model, which is the point of the check).
"""
from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cases import (REPO, amplitude_tag, case_ids, level_names, load_case,  # noqa: E402
                   load_settings, validity_fraction)
from fem_core import Level  # noqa: E402


def tet_volumes(X: np.ndarray, T: np.ndarray) -> np.ndarray:
    """Signed volumes of the tetrahedra T (nel, 4) with node coordinates X (n, 3)."""
    a, b, c = X[T[:, 1]] - X[T[:, 0]], X[T[:, 2]] - X[T[:, 0]], X[T[:, 3]] - X[T[:, 0]]
    return np.einsum("ij,ij->i", a, np.cross(b, c)) / 6.0


def volume_ratios(X: np.ndarray, T: np.ndarray, U: np.ndarray, a: float = 1.0) -> np.ndarray:
    """Volume of every tetrahedron after displacing the nodes by a*U, divided by its volume at rest."""
    return tet_volumes(X + a * U, T) / tet_volumes(X, T)


def selftest(L: Level) -> list[tuple[str, bool, str]]:
    X, T = L.mesh.p.T, L.mesh.t.T
    res = []
    r = volume_ratios(X, T, X, 0.1)                       # uniform dilation: ratio (1 + 0.1)^3
    err = float(abs(r - 1.1 ** 3).max())
    res.append(("uniform dilation gives (1+e)^3", err < 1e-12, f"{err:.1e}"))
    U = np.zeros_like(X); U[:, 0] = -2 * X[:, 0]          # reflection x -> -x: every ratio is -1
    err = float(abs(volume_ratios(X, T, U) + 1).max())
    res.append(("reflection gives ratio -1 (inversion is detected)", err < 1e-12, f"{err:.1e}"))
    # linearisation: for a tiny amplitude, ratio - 1 equals the divergence of the FE displacement field
    case = load_case(L.name, "cap_medium", L.ns)
    U1 = L.solve_full(case.pattern())
    a = 1e-3
    grad = L.basis.interpolate(U1.reshape(-1)).grad        # (3, 3, nel, nq), constant per P1 element
    div = (grad[0, 0] + grad[1, 1] + grad[2, 2])[:, 0]
    dev = float(abs((volume_ratios(X, T, U1, a) - 1) / a - div).max() / abs(div).max())
    res.append(("ratio - 1 matches the FE divergence for a small amplitude", dev < 1e-3, f"{dev:.1e}"))
    return res


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--levels", nargs="*", help="default: all levels")
    ap.add_argument("--out-dir", type=Path, default=REPO / "results" / "ours" / "analysis")
    ap.add_argument("--static-dir", type=Path, default=REPO / "results" / "ours" / "static",
                    help="if it holds result files, they are compared with the solutions used here")
    ap.add_argument("--plot-level", default="fine")
    args = ap.parse_args()

    S = load_settings()
    levels = args.levels or level_names(S)
    cases = case_ids(S, physical_only=True)
    forces = sorted(set(S["static"]["forces_N"]) | set(S["static"].get("nonlinear_check_forces_N", [])))
    R = float(S["geometry"]["radius_m"])
    frac, _ = validity_fraction(S)
    args.out_dir.mkdir(parents=True, exist_ok=True)

    rows, data = [], {}
    for lv in levels:
        L = Level(lv, S)
        print(f"[{lv}] {L.N // 3} nodes, {L.mesh.t.shape[1]} tetrahedra")
        for name, ok, detail in selftest(L):
            print(f"  {'PASS' if ok else 'FAIL'}  {name} | {detail}")
            if not ok:
                raise SystemExit("self-test failed")
        X, T = L.mesh.p.T, L.mesh.t.T
        link = 0.0
        for c in cases:
            U1 = L.solve_full(load_case(lv, c, L.ns).pattern())          # (n_nodes, 3) per newton
            pk1 = float(np.linalg.norm(U1[L.sn], axis=1).max())
            f = args.static_dir / f"{lv}_{c}_{amplitude_tag(1)}.npy"
            if f.exists():                                                # same solution as run_static.py
                link = max(link, float(abs(np.load(f) - U1[L.sn]).max() / abs(U1[L.sn]).max()))
            for F in forces:
                r = volume_ratios(X, T, U1, float(F))
                rows.append([lv, c, F, f"{F * pk1 * 1e3:.6g}", f"{F * pk1 / R:.6g}", str(F * pk1 > frac * R).lower(),
                             f"{r.min():.6g}", f"{np.percentile(r, 1):.6g}", f"{r.max():.6g}",
                             f"{np.mean(r < 0.9):.6g}", int((r <= 0).sum()), len(r)])
                data[(lv, c, F)] = (float(r.min()), float(np.percentile(r, 1)))
        if (args.static_dir / f"{lv}_{cases[0]}_{amplitude_tag(1)}.npy").exists():
            print(f"  PASS  solutions equal the static result files | max relative difference {link:.1e}")
            if link > 1e-12:
                raise SystemExit("volume-check solutions differ from results/ours/static")
        else:
            print("  note  no static result files found; not compared")

    with open(args.out_dir / "volume_ratio.csv", "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["level", "case", "force_N", "peak_disp_mm", "peak_disp_fraction_of_radius", "beyond_validity",
                    "min_volume_ratio", "p01_volume_ratio", "max_volume_ratio", "fraction_ratio_below_0.9",
                    "n_inverted_tets", "n_tets"])
        w.writerows(rows)

    pl = args.plot_level if args.plot_level in levels else levels[-1]
    fig, axes = plt.subplots(1, 2, figsize=(12, 5), sharex=True)
    cmap = plt.get_cmap("tab10")
    allc = case_ids(S)
    for k, (ax, what) in enumerate(zip(axes, ["smallest ratio over all tetrahedra",
                                              "1st percentile of the ratio"])):
        for c in cases:
            ax.plot(forces, [data[(pl, c, F)][k] for F in forces], "o-", ms=4, color=cmap(allc.index(c) % 10), label=c)
        ax.axhline(1.0, color="gray", lw=0.8); ax.axhline(0.0, color="k", ls="--", lw=1)
        ax.set_xscale("log"); ax.set_xlabel("applied force [N] (above the static list: extrapolation)")
        ax.set_ylabel("tetrahedron volume ratio (displaced / rest)"); ax.set_title(what, fontsize=10)
        ax.grid(True, which="both", alpha=0.25)
    axes[0].text(forces[0], 0.03, "ratio <= 0: inverted tetrahedron", fontsize=8)
    axes[0].legend(fontsize=8, ncol=2, loc="lower left")
    fig.suptitle(f"Local volume change of the linear solution ({pl} mesh)")
    fig.tight_layout(); fig.savefig(args.out_dir / "fig5_volume_ratio.png", dpi=200); plt.close(fig)
    print("wrote volume_ratio.csv, fig5_volume_ratio.png in", args.out_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main())
