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
1. Python 3.10+ (tested with 3.12; 3.13 is untested). `pip install -r solver/requirements.txt` installs `scikit-fem`, `meshio`, `numpy`, `scipy`. The prototype used scikit-fem 12.0.2; any recent version is fine. Do not hand-write the element maths. Run the scripts as `python solver/<script>.py`; they import each other by plain module name.
2. `fem_core.py` (class `Level`): load `ball.msh` with `MeshTet.load` and `surface.json`; assert that the counts and every node coordinate equal the file as read by meshio and that the surface positions match (open item O2, scikit-fem half). Build `Basis(mesh, ElementVector(ElementTetP1()))` and assert that the DOF layout is node-major interleaved (`nodal_dofs == arange(N).reshape(-1, 3).T`); it is in scikit-fem 12.0.2, and the code stops with an error if it is not.
3. Assemble the stiffness matrix with `skfem.models.elasticity.linear_elasticity` and `lame_parameters(E0, nu)`, where `E0 = E_inf + E1` and `nu` come from the settings. Remove the DOFs of `fixed_nodes`. Factorize once per level (`scipy.sparse.linalg.splu`).
4. `cases.py`: read a case file and return its loads as a dense surface-DOF vector `p` of length `3*n_surface` (CONTRACT 4.2); it checks that rows are unique and ascending and that the sum of the force norms is 1. It also loads the settings, the surface data and the validity fraction.
5. `Level` exposes `solve_loads(F_s)` (one back-substitution, returns the surface displacement `(n_surface, 3)`), `influence_blocks(block)` (generator of column blocks of `G` in float64, in column order; this is what 2b exports), `G_columns(cols)`, `influence_times(p)` (`G p` from the loaded columns only), `build_influence()` (the full `G`, refused above 500 MB unless forced: 638 MB in float64 on the fine level) and `selftest()`. Columns of fixed rows and fixed rows of every column are zero.
6. `run_static.py`: for every level, case and amplitude in `static.forces_N`, write `results/ours/static/<level>_<case>_<F>N.npy` = `F * u` (float64, `(n_surface, 3)`), where `u` is one direct back-substitution for the unit case pattern. This equals `G p`; the full `G` is not built. Options: `--levels`, `--cases`, `--out-dir`, `--full-check`, `--skip-selftest`.
7. Self-checks (fail loudly). Independent of `G`, because `G` and the direct solve share the stiffness matrix:
   - material law: a unit cube under uniaxial traction reproduces the analytic solution (`u_x = s x/E0`, `u_y = -nu s y/E0`);
   - `K` is symmetric; rigid translations and rotations produce no force;
   - reactions at the fixed nodes balance the applied load; the energy identity `1/2 F.u = 1/2 u'Ku` holds;
   - fixed nodes have zero displacement; columns and rows of `G` that belong to fixed nodes are zero;
   - `G` is symmetric on a random sub-block (`max|G - G.T| / max|G| < 1e-10`);
   - doubling the load doubles the displacement;
   - for every case, the direct result equals `G p` built from the loaded columns to 1e-9 relative. Cases that need more than 4000 solves (`pressure_all` on `fine`) are skipped unless `--full-check` is given;
   - output files: all exist, float64, correct shape, no NaN, linear in the amplitude.
   The run also prints, for each case, the peak displacement per newton on every level, the change medium to fine, the force at which the peak reaches the validity limit (`validity.max_disp_fraction_of_radius` x radius), and the percentage of the radius at the largest amplitude. Give these numbers to Role 4.
8. **Stage 2 (only if `stage2.enabled`):** `viscoelastic.py` implements the update of CONTRACT 4.5 per force component (and the Kelvin-Voigt first-order lag if `stage2.model == "kv"`; see O1). `run_dynamic.py` reads the profile CSVs, scales the case pattern by the profile, steps at `dt_force`, and writes `results/ours/dynamic/<level>_<case>_<profile>.npz` sampled at `dt_out`. Unit tests: a step force gives `f_eff(0+) = f`; a constant force tends to `f*E0/E_inf`; the update agrees with a fine-step numerical solution of the ODE to better than 1e-8.

### Acceptance checks
- All self-checks in task 7 pass.
- Static result files exist for every level x case x amplitude; shape `(n_surface, 3)`, float64, no NaN.
- `python solver/run_static.py` from a clean checkout reproduces the results.
- `python solver/run_static.py --levels fine --cases pressure_all --full-check` passes (slow; run it once).
- Stage 2 only: the unit tests above pass.

### Not your job
Meshing (1a), SOFA (3), engine code, test values and cases (4).

## Role 2b: Export, benchmark and C# core

### Inputs
2a's `fem_core.py` (`build_influence`, `solve_loads`) and `cases.py`, `config/test_settings.json`, `model/<level>/surface.json`, `config/cases/`, `config/profiles/*.csv`.

### Deliverables
`solver/export.py`, `solver/benchmark.py`, `solver/check_parity.py`, `solver/export/<level>/G.bin` + `G.meta.json` (the `.bin` files are not committed), timing files in `results/ours/timing/`, and the C# library in `core/` (`core/src/`, `core/tests/`).

### Tasks (in order)
1. `export.py`: for each level, build `G`, convert to float32 and write `G.bin` (column-major) and `G.meta.json` exactly as in CONTRACT 6.5. Print file sizes and the build time. `python solver/export.py` must regenerate everything from `model/` and `config/`. Use `Level.influence_blocks()` from `fem_core.py`: it yields the columns of `G` in order, so `G.bin` can be written block by block without holding the whole matrix (638 MB in float64 on `fine`).
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
