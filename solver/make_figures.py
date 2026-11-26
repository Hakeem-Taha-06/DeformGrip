"""Figures and tables from the static results of "ours" (Role 2a, no solving).

Reads  results/ours/static/*.npy (written by run_static.py), model/<level>/surface.json
and config/test_settings.json, and writes to results/ours/analysis/:
    fig1_peak_vs_force.png      peak surface displacement versus force, physical cases
    fig2_validity_limits.png    force at which each case reaches the validity limit
    fig3_mesh_convergence.png   peak displacement per newton versus mesh size
    fig4_displacement_maps.png  displacement magnitude on the surface for selected cases
    static_summary.csv, mesh_convergence.csv, validity_limits.csv

Usage: python solver/make_figures.py [--level fine] [--map-level medium] [--map-force 10]
                                     [--static-dir DIR] [--out-dir DIR]
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
from matplotlib.cm import ScalarMappable  # noqa: E402
from matplotlib.colors import Normalize  # noqa: E402
from mpl_toolkits.mplot3d.art3d import Poly3DCollection  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cases import (REPO, amplitude_tag, case_ids, level_names, load_settings,  # noqa: E402
                   load_surface, validity_fraction)

DPI = 200
MAP_CASES = ["cap_medium", "cap_shear", "cap_side", "cap_lower"]


def load_static(static_dir: Path, level: str, case: str, force) -> np.ndarray:
    f = static_dir / f"{level}_{case}_{amplitude_tag(force)}.npy"
    if not f.exists():
        raise SystemExit(f"missing {f}\nrun `python solver/run_static.py` first (or pass --static-dir)")
    return np.load(f, allow_pickle=False)


def peak(u: np.ndarray) -> float:
    return float(np.linalg.norm(u, axis=1).max())


def style(settings: dict, cases: list[str]) -> dict:
    cmap = plt.get_cmap("tab10")
    return {c: cmap(i % 10) for i, c in enumerate(case_ids(settings, physical_only=False)) if c in cases}


def collect(settings: dict, static_dir: Path, levels, cases):
    """peak[level][case][force] in metres, from the result files."""
    forces = settings["static"]["forces_N"]
    return {lv: {c: {F: peak(load_static(static_dir, lv, c, F)) for F in forces} for c in cases} for lv in levels}


def per_newton(peaks_case: dict) -> float:
    F0 = min(peaks_case)                       # smallest amplitude, the model is linear
    return peaks_case[F0] / F0                 # m per N


def write_csv(path: Path, header, rows):
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(header)
        w.writerows(rows)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--level", default="fine", help="level for figures 1 and 2 (default: fine)")
    ap.add_argument("--map-level", default="medium", help="level for figure 4 (default: medium)")
    ap.add_argument("--map-force", type=float, default=10, help="amplitude for figure 4, must be in static.forces_N")
    ap.add_argument("--static-dir", type=Path, default=REPO / "results" / "ours" / "static")
    ap.add_argument("--out-dir", type=Path, default=REPO / "results" / "ours" / "analysis")
    args = ap.parse_args()

    S = load_settings()
    R = float(S["geometry"]["radius_m"])
    frac, from_settings = validity_fraction(S)
    limit_m = frac * R
    levels = level_names(S)
    cases = case_ids(S, physical_only=True)
    forces = S["static"]["forces_N"]
    amax = max(forces)
    mf = next((F for F in forces if float(F) == args.map_force), None)
    if mf is None:
        raise SystemExit(f"--map-force {args.map_force} is not in static.forces_N {forces}")
    args.out_dir.mkdir(parents=True, exist_ok=True)
    col = style(S, cases)
    P = collect(S, args.static_dir, levels, cases)
    nodes = {lv: load_surface(lv)["n_nodes"] for lv in levels}
    print(f"validity limit: {frac:.0%} of the radius = {limit_m * 1e3:.2f} mm"
          + ("" if from_settings else " (built-in default; no 'validity' key in the settings)"))

    # ---- tables
    rows = []
    for lv in levels:
        for c in cases:
            for F in forces:
                pk = P[lv][c][F]
                rows.append([lv, c, F, f"{pk * 1e3:.6g}", f"{pk / R:.6g}", str(pk > limit_m).lower()])
    write_csv(args.out_dir / "static_summary.csv",
              ["level", "case", "force_N", "peak_disp_mm", "peak_disp_fraction_of_radius", "beyond_validity"], rows)
    write_csv(args.out_dir / "mesh_convergence.csv",
              ["case"] + [f"peak_mm_per_N_{lv}" for lv in levels] + [f"change_vs_{levels[-1]}_pct_{lv}" for lv in levels[:-1]],
              [[c] + [f"{per_newton(P[lv][c]) * 1e3:.6g}" for lv in levels]
               + [f"{100 * (per_newton(P[lv][c]) / per_newton(P[levels[-1]][c]) - 1):+.4g}" for lv in levels[:-1]] for c in cases])
    write_csv(args.out_dir / "validity_limits.csv",
              ["case"] + [f"limit_N_{lv}" for lv in levels] + [f"percent_of_radius_at_{amax}N_{lv}" for lv in levels],
              [[c] + [f"{limit_m / per_newton(P[lv][c]):.6g}" for lv in levels]
               + [f"{100 * per_newton(P[lv][c]) * amax / R:.6g}" for lv in levels] for c in cases])

    lv = args.level
    # ---- figure 1
    fig, ax = plt.subplots(figsize=(7.5, 5))
    xs = np.array([min(forces) / 2, amax * 1.5])
    for c in cases:
        s = per_newton(P[lv][c]) * 1e3
        ax.plot(xs, s * xs, color=col[c], lw=1.2, alpha=0.8)
        ax.plot(forces, [P[lv][c][F] * 1e3 for F in forces], "o", color=col[c], ms=4, label=c)
    ax.axhline(limit_m * 1e3, color="k", ls="--", lw=1)
    ax.text(xs[0], limit_m * 1e3 * 1.12, f"validity limit: {frac:.0%} of radius = {limit_m * 1e3:.1f} mm", fontsize=8)
    ax.set_xscale("log"); ax.set_yscale("log")
    ax.set_xlabel("applied force [N]"); ax.set_ylabel("peak surface displacement [mm]")
    ax.set_title(f"Peak surface displacement versus force ({lv} mesh, linear elasticity)")
    ax.grid(True, which="both", alpha=0.25); ax.legend(fontsize=8, ncol=2, loc="lower right")
    fig.tight_layout(); fig.savefig(args.out_dir / "fig1_peak_vs_force.png", dpi=DPI); plt.close(fig)

    # ---- figure 2
    lims = {c: limit_m / per_newton(P[lv][c]) for c in cases}
    order = sorted(cases, key=lambda c: lims[c])
    fig, ax = plt.subplots(figsize=(7.5, 4.5))
    ax.barh(order, [lims[c] for c in order], color=["tab:red" if lims[c] < amax else "tab:blue" for c in order])
    for i, c in enumerate(order):
        ax.text(lims[c] * 1.05, i, f"{lims[c]:.1f} N", va="center", fontsize=8)
    ax.axvline(amax, color="k", ls="--", lw=1)
    ax.text(amax * 1.05, 0, f"largest static amplitude ({amax} N)", fontsize=8, va="center")
    ax.set_xscale("log"); ax.set_xlim(min(lims.values()) / 1.5, max(lims.values()) * 4)
    ax.set_xlabel(f"force at which the peak displacement reaches {frac:.0%} of the radius [N]")
    ax.set_title(f"Force range of the linear model by case ({lv} mesh); red: limit below {amax} N")
    ax.grid(True, axis="x", which="both", alpha=0.25)
    fig.tight_layout(); fig.savefig(args.out_dir / "fig2_validity_limits.png", dpi=DPI); plt.close(fig)

    # ---- figure 3
    fig, ax = plt.subplots(figsize=(7.5, 4.8))
    xn = [nodes[l] for l in levels]
    for c in cases:
        ref = per_newton(P[levels[-1]][c])
        ax.plot(xn, [100 * (per_newton(P[l][c]) / ref - 1) for l in levels], "o-", color=col[c], ms=4, label=c)
    ax.axhline(0, color="k", lw=0.8)
    ax.set_xscale("log"); ax.set_xticks(xn); ax.set_xticklabels([f"{l}\n{n} nodes" for l, n in zip(levels, xn)])
    ax.set_ylabel(f"change of peak displacement per newton\nrelative to {levels[-1]} [%]")
    ax.set_title("Mesh convergence of the peak displacement (physical cases)")
    ax.grid(True, alpha=0.25); ax.legend(fontsize=8, ncol=2)
    fig.tight_layout(); fig.savefig(args.out_dir / "fig3_mesh_convergence.png", dpi=DPI); plt.close(fig)

    # ---- figure 4
    ml = args.map_level
    surf = load_surface(ml)
    pos, tri = surf["positions"], surf["triangles"]
    spec_all = S["cases"]["list"]
    fig = plt.figure(figsize=(11, 9))
    for k, c in enumerate(MAP_CASES):
        if c not in cases:
            continue
        spec = spec_all[c]
        u = load_static(args.static_dir, ml, c, mf)
        mag = np.linalg.norm(u, axis=1) * 1e3
        elev, azim = 90 - spec["polar_deg"], spec["azimuth_deg"]
        v = np.array([np.cos(np.radians(elev)) * np.cos(np.radians(azim)),
                      np.cos(np.radians(elev)) * np.sin(np.radians(azim)), np.sin(np.radians(elev))])
        a, b, d = pos[tri[:, 0]], pos[tri[:, 1]], pos[tri[:, 2]]
        nrm = np.cross(b - a, d - a)
        nrm *= np.sign(np.einsum("ij,ij->i", nrm, a + b + d))[:, None]       # outward, whatever the winding
        vis = nrm @ v > 0
        norm = Normalize(vmin=0, vmax=mag.max())
        face = plt.get_cmap("viridis")(norm(mag[tri[vis]].mean(axis=1)))
        ax = fig.add_subplot(2, 2, k + 1, projection="3d")
        ax.add_collection3d(Poly3DCollection(pos[tri[vis]], facecolors=face, edgecolors="none"))
        r = float(np.abs(pos).max())
        ax.set_xlim(-r, r); ax.set_ylim(-r, r); ax.set_zlim(-r, r); ax.set_box_aspect((1, 1, 1))
        ax.plot([1.04 * r * v[0]], [1.04 * r * v[1]], [1.04 * r * v[2]], "x", color="white", mec="black",
                ms=9, mew=2.5, zorder=20)                                      # contact centre
        ax.view_init(elev=elev, azim=azim); ax.set_axis_off()
        d_txt = spec["direction"] if isinstance(spec["direction"], str) else "direction " + str(spec["direction"])
        beyond = mag.max() * 1e-3 > limit_m
        ax.set_title(f"{c}: {mf} N, peak {mag.max():.2f} mm ({100 * mag.max() * 1e-3 / R:.0f}% of R)"
                     + ("\nbeyond validity limit" if beyond else "")
                     + f"\npolar {spec['polar_deg']} deg, azimuth {spec['azimuth_deg']} deg, "
                       f"half-angle {spec['half_angle_deg']} deg, {d_txt}",
                     fontsize=9, color="tab:red" if beyond else "black")
        fig.colorbar(ScalarMappable(norm=norm, cmap="viridis"), ax=ax, shrink=0.6, pad=0.02, label="|u| [mm]")
    fig.suptitle(f"Surface displacement magnitude on the undeformed {ml} mesh (viewed towards the contact centre, marked x)")
    fig.tight_layout(); fig.savefig(args.out_dir / "fig4_displacement_maps.png", dpi=DPI); plt.close(fig)

    print("wrote:", ", ".join(sorted(p.name for p in args.out_dir.iterdir() if p.name.startswith(("fig", "static", "mesh", "validity")))))
    return 0


if __name__ == "__main__":
    sys.exit(main())
