"""Static results of "ours" (Role 2a).

For every mesh level, case and amplitude in `static.forces_N` this writes
    results/ours/static/<level>_<case>_<F>N.npy      float64, shape (n_surface, 3), metres
Each case needs one back-substitution (u = K^-1 p for the unit pattern p, which equals G p);
the result for amplitude a is a * u (CONTRACT 4.3).

Usage:  python solver/run_static.py [--levels coarse medium] [--cases cap_small ...]
                                    [--out-dir DIR] [--full-check] [--skip-selftest]
"""
from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cases import (REPO, amplitude_tag, case_ids, level_names, load_case,  # noqa: E402
                   load_settings, validity_fraction)
from fem_core import Level, raise_on_failure, selftest_cube  # noqa: E402

G_CHECK_MAX_COLS = 4000      # skip the G-vs-direct check above this many solves unless --full-check


def report(results) -> None:
    for name, ok, detail in results:
        print(f"  {'PASS' if ok else 'FAIL'}  {name} | {detail}")
    raise_on_failure(results)


def check_case(L: Level, case, u1: np.ndarray, full_check: bool) -> list[tuple[str, bool, str]]:
    p = case.pattern()
    tag = f"{L.name}/{case.case_id}"
    out = []
    out.append((f"{tag}: finite, fixed nodes zero",
                bool(np.isfinite(u1).all() and np.all(u1[L.fixed_rows] == 0.0)), ""))
    u2 = L.solve_loads(2.0 * p)
    lin = float(abs(u2 - 2.0 * u1).max() / abs(u1).max())
    out.append((f"{tag}: doubling the load doubles the displacement", lin < 1e-12, f"{lin:.1e}"))
    cols = L.live_columns(p)
    if cols > G_CHECK_MAX_COLS and not full_check:
        out.append((f"{tag}: direct solve equals G p", True, f"skipped ({cols} columns; use --full-check)"))
    else:
        rel = float(np.linalg.norm(L.influence_times(p) - u1) / np.linalg.norm(u1))
        out.append((f"{tag}: direct solve equals G p", rel < 1e-9, f"{rel:.1e} over {cols} columns"))
    return out


def verify_outputs(out_dir: Path, settings: dict, levels, cases, ns_by_level) -> list[tuple[str, bool, str]]:
    amps = settings["static"]["forces_N"]
    missing, bad, lin = [], [], 0.0
    for lvl in levels:
        for cid in cases:
            arrs = []
            for a in amps:
                f = out_dir / f"{lvl}_{cid}_{amplitude_tag(a)}.npy"
                if not f.exists():
                    missing.append(f.name)
                    continue
                x = np.load(f, allow_pickle=False)
                if x.shape != (ns_by_level[lvl], 3) or x.dtype != np.float64 or not np.isfinite(x).all():
                    bad.append(f.name)
                    continue
                arrs.append(x / a)
            if arrs:
                lin = max(lin, max(float(abs(x - arrs[0]).max() / abs(arrs[0]).max()) for x in arrs))
    n = len(levels) * len(cases) * len(amps)
    return [(f"output files ({n} expected)", not missing and not bad,
             f"missing {len(missing)}, bad shape/dtype/NaN {len(bad)}"),
            ("output files are linear in the amplitude", lin < 1e-12, f"{lin:.1e}")]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--levels", nargs="*", help="default: all levels in the settings")
    ap.add_argument("--cases", nargs="*", help="default: all cases in the settings")
    ap.add_argument("--out-dir", type=Path, default=REPO / "results" / "ours" / "static")
    ap.add_argument("--full-check", action="store_true", help="also run the G check for very large cases (slow)")
    ap.add_argument("--skip-selftest", action="store_true")
    args = ap.parse_args()

    settings = load_settings()
    levels = args.levels or level_names(settings)
    cases = args.cases or case_ids(settings)
    amps = settings["static"]["forces_N"]
    frac, from_settings = validity_fraction(settings)
    R = float(settings["geometry"]["radius_m"])
    args.out_dir.mkdir(parents=True, exist_ok=True)

    if not args.skip_selftest:
        print("material-law check")
        report(selftest_cube(settings))

    peaks: dict[tuple[str, str], float] = {}
    ns_by_level: dict[str, int] = {}
    for lvl in levels:
        t0 = time.perf_counter()
        L = Level(lvl, settings)
        ns_by_level[lvl] = L.ns
        print(f"\n[{lvl}] {L.N // 3} nodes, {L.ns} surface rows, {len(L.fixed_nodes)} fixed; "
              f"assemble {L.t_assemble:.2f} s, factorize {L.t_factor:.2f} s")
        if not args.skip_selftest:
            report(L.selftest())
        for cid in cases:
            case = load_case(lvl, cid, L.ns)
            u1 = L.solve_loads(case.pattern())
            report(check_case(L, case, u1, args.full_check))
            peaks[(lvl, cid)] = float(np.linalg.norm(u1, axis=1).max())     # m per N
            for a in amps:
                np.save(args.out_dir / f"{lvl}_{cid}_{amplitude_tag(a)}.npy", float(a) * u1, allow_pickle=False)
        print(f"[{lvl}] done in {time.perf_counter() - t0:.1f} s")

    print("\noutput verification")
    report(verify_outputs(args.out_dir, settings, levels, cases, ns_by_level))

    # numbers for Role 4: peak displacement per newton, convergence, validity limit
    amax = max(amps)
    print(f"\nPeak surface displacement per newton [mm/N] and the force at which it reaches "
          f"{frac:.0%} of the radius ({frac * R * 1e3:.2f} mm)"
          + ("" if from_settings else "  [fraction is the built-in default; settings have no 'validity' key]"))
    head = "  case          " + "".join(f"{lvl:>10s}" for lvl in levels)
    if "medium" in levels and "fine" in levels:
        head += "  med->fine"
    print(head + f"  limit [N] ({levels[-1]})  % of R at {amax} N")
    for cid in cases:
        row = f"  {cid:14s}" + "".join(f"{peaks[(l, cid)] * 1e3:10.4f}" for l in levels)
        if "medium" in levels and "fine" in levels:
            if settings["cases"]["list"][cid]["type"] == "random":
                row += "       n/a"
            else:
                row += f"  {100 * (peaks[('fine', cid)] / peaks[('medium', cid)] - 1):+8.2f}%"
        pk = peaks[(levels[-1], cid)]
        row += f"  {frac * R / pk:12.1f}      {100 * pk * amax / R:8.2f}"
        print(row)
    print("(random_* cases pick different rows on every mesh, so their peaks are not comparable across levels)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
