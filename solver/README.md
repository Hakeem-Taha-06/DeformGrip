# Role 2: Solver (2a) and Export + C# Core (2b)

> Two people share this folder. **2a** writes our solver; **2b** precomputes, exports, and writes the C# core. Read `../CONTRACT.md` first; it is authoritative. If this file contradicts it, tell the user.

## Purpose
- **2a** writes our own calculation: given forces on the ball surface, work out how far every surface point moves. It uses a ready-made finite-element library, so the element maths is not written by hand. 2a also builds the influence matrix `G`.
- **2b** ships `G` to the game engine and writes the small plain-C# core that the engine calls each frame. The core does no solve: it adds up columns of `G`.
- File split (two people never edit the same file): 2a owns `fem_core.py`, `cases.py`, `run_static.py`, `viscoelastic.py`, `run_dynamic.py`, `requirements.txt`. 2b owns `export.py`, `benchmark.py`, `check_parity.py`, `export/`, and the whole `core/` folder.

## Role 2a: Solver core

### Inputs
`config/test_settings.json`, `model/<level>/ball.msh`, `model/<level>/surface.json`, `config/cases/<level>/*.json`, `config/profiles/*.csv` (Stage 2).

### Deliverables
`solver/fem_core.py`, `solver/cases.py`, `solver/run_static.py`, `solver/requirements.txt`, results in `results/ours/static/`. Stage 2 (only if enabled): `solver/viscoelastic.py`, `solver/run_dynamic.py`, results in `results/ours/dynamic/`.

### Tasks (in order)
1. Python 3.10+. Requirements: `scikit-fem`, `meshio`, `numpy`, `scipy`. The prototype used scikit-fem 12.0.2; any recent version is fine. Do not hand-write the element maths.
2. `fem_core.py`: load `ball.msh` with `MeshTet.load`; load `surface.json`; assert the node coordinates equal the file's (open item O2). Build `Basis(mesh, ElementVector(ElementTetP1()))`. **Assert the DOF layout** is node-major interleaved: `basis.nodal_dofs[:, :5] == np.arange(15).reshape(5, 3).T`; if not, build an explicit permutation so every array follows CONTRACT 3.
3. Assemble the stiffness matrix with `skfem.models.elasticity.linear_elasticity` and `lame_parameters(E0, nu)`, where `E0 = E_inf + E1` and `nu` come from the settings. Remove the DOFs of `fixed_nodes`. Factorize once per level (`scipy.sparse.linalg.splu`).
4. `cases.py`: read a case file and return its loads as a dense surface-DOF vector `p` of length `3*n_surface` (CONTRACT 4.2).
5. Expose `solve_loads(level, F_s)` (direct solve for a surface load vector, returns the surface displacement `(n_surface, 3)`) and `build_influence(level)`: the matrix `G` of CONTRACT 4.3 in float64, built by back-substitution for every surface DOF (in blocks of right-hand sides; columns of fixed rows are zero).
6. `run_static.py`: for every level, case and amplitude in `static.forces_N`, write `results/ours/static/<level>_<case>_<F>N.npy` = `F * (G p)` reshaped to `(n_surface, 3)` (float64).
7. Self-checks inside `run_static.py` (fail loudly):
   - fixed nodes have zero displacement;
   - `G` is symmetric: `max|G - G.T| / max|G| < 1e-10`;
   - doubling the amplitude doubles the displacement;
   - for every case at one amplitude per level, a **direct** solve of `K u = F` with the real force vector (no `G`) equals the `G` result to 1e-9 relative;
   - mesh convergence: print the largest displacement per newton of each physical case for each level and the change medium to fine (give these numbers to Role 4).
8. **Stage 2 (only if `stage2.enabled`):** `viscoelastic.py` implements the update of CONTRACT 4.5 per force component (and the Kelvin-Voigt first-order lag if `stage2.model == "kv"`; see O1). `run_dynamic.py` reads the profile CSVs, scales the case pattern by the profile, steps at `dt_force`, and writes `results/ours/dynamic/<level>_<case>_<profile>.npz` sampled at `dt_out`. Unit tests: a step force gives `f_eff(0+) = f`; a constant force tends to `f*E0/E_inf`; the update agrees with a fine-step numerical solution of the ODE to better than 1e-8.

### Acceptance checks
- All self-checks in task 7 pass.
- Static result files exist for every level x case x amplitude; shape `(n_surface, 3)`, float64, no NaN.
- `python solver/run_static.py` from a clean checkout reproduces the results.
- Stage 2 only: the unit tests above pass.

### Not your job
Meshing (1a), SOFA (3), engine code, test values and cases (4).

## Role 2b: Export, benchmark and C# core

### Inputs
2a's `fem_core.py` (`build_influence`, `solve_loads`) and `cases.py`, `config/test_settings.json`, `model/<level>/surface.json`, `config/cases/`, `config/profiles/*.csv`.

### Deliverables
`solver/export.py`, `solver/benchmark.py`, `solver/check_parity.py`, `solver/export/<level>/G.bin` + `G.meta.json` (the `.bin` files are not committed), timing files in `results/ours/timing/`, and the C# library in `core/` (`core/src/`, `core/tests/`).

### Tasks (in order)
1. `export.py`: for each level, build `G`, convert to float32 and write `G.bin` (column-major) and `G.meta.json` exactly as in CONTRACT 6.5. Print file sizes and the build time. `python solver/export.py` must regenerate everything from `model/` and `config/`.
2. `benchmark.py` writes `results/ours/timing/<level>_<case>.json` for the `timing_cases`. It measures and reports separately, so the speed-up is honest:
   - (i) assemble + factorize + solve from scratch;
   - (ii) one back-substitution with the factorization cached (what a normal FEM would pay per frame);
   - (iii) the precomputed runtime step in NumPy: `G` applied to the case's nonzero load components (and the Stage 2 state update if enabled);
   - the one-off precompute time (building `G`) and the exported file size.
   Put each of (i)-(iii) in its own entry of `measurements` with its own `what_is_timed` (CONTRACT 6.6), add `precompute_s` and `export_bytes`, and record the hardware.
3. **C# core** in `core/`: a class library (`core/*.csproj`, sources in `core/src/`) with plain C# and no Godot references and no JSON parsing (1b passes numbers in). Target the framework that the pinned Godot version uses (see `godot/VERSION.txt`; open item O5). Required types:
   - `NodalLoad`: `int Row; double Fx, Fy, Fz`.
   - `InfluenceMatrix`: `Load(string gBinPath, int nSurface)` reads the column-major float32 file; `Apply(ReadOnlySpan<NodalLoad> loads, float gain, float[] outDisp)` writes `gain * sum f * G[:, 3*row + c]` into `outDisp` (length `3*nSurface`, interleaved xyz). Skip zero components.
   - Stage 2 only: a viscoelastic state class that holds one state per force component, steps with the exact update of CONTRACT 4.5 (`Step(...)` with `fPrev`, `fNext`, `dt`), keeps relaxing after a load is removed, and exposes an "elastic" mode in which the effective force equals the input. Design its data structure yourself; it must meet the parity checks below.
   Keep the code short and commented.
4. **Tests without Godot:** a console test project in `core/tests/` that loads a `G.bin`, applies a case, and prints values to compare with Python (and, for Stage 2, runs the viscoelastic class on a profile).
5. `check_parity.py`: compare `results/godot/dumps/ours_*.bin` (from 1b) with a Python reference (`G` applied to the profile-scaled case, or `viscoelastic.py` output in Stage 2) and print the max relative error. Also compare the C# test output of task 4 with Python.
6. Review 1b's use of the core once (it builds the load set per frame; the vertex update happens once per rendered frame).

### Acceptance checks
- `export.py` writes `G.bin` + `G.meta.json` for all 3 levels; the length of each `G.bin` is `4 * (3*n_surface)^2` bytes; `G.bin` read back (float64) equals 2a's `G` within float32 precision (about 1e-6 relative).
- Python `10 * (G p)` from the file equals `results/ours/static/<level>_<case>_10N.npy` within float32 precision for every case.
- Benchmark files contain all of (i), (ii), (iii) with `what_is_timed` filled in.
- C# core output equals Python within 1e-5 relative; Godot dumps agree within 1e-4 relative.
- `core/` builds without any Godot assembly.

### Not your job
Mesh creation (1a), the scene and its UI (1b), SOFA (3), choosing test values and cases (4).
