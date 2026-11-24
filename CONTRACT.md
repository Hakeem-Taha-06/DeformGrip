# DeformGrip: Shared Contract

This file is authoritative for all roles. Role READMEs refer to it by section number. If a README contradicts this file, this file wins: do not pick silently, tell the user. Changes to this file are made only by the user, so ask first rather than working around it.

## 1. Project summary

We simulate pressing on a soft rubber ball (a grip-rehabilitation game object) in real time, with arbitrary 3D forces applied anywhere on its surface. The model is small-strain linear elasticity on a tetrahedral mesh (**Stage 1, mandatory**), optionally extended with one-term Prony-series viscoelasticity (**Stage 2, only if time allows**).

Two implementations of the same model are built:
- **ours:** offline precomputation of the surface influence matrix `G` in Python (scikit-fem); runtime in Godot with a plain-C# core, where one frame costs a weighted sum of columns of `G`.
- **SOFA reference:** SOFA Framework, run offline.

The final deliverable is a results report comparing accuracy and speed of the two.

Input in this phase: sets of nodal loads defined in `config/cases/` and scaled by an amplitude (manual slider or replayed force profile). The application core accepts arbitrary nodal loads (section 4.4). A custom VR controller supplied externally will produce such loads later; how it determines them is out of scope here. Clinical claims are out of scope.

Team constraints: 6 people, each with roughly 4-5 hours per week, and not everyone is comfortable with the game engine. Choose the simplest option that satisfies this contract. Do not add features that are not listed.

## 2. Roles and ownership

| Role | Person | Owns (write only here) | Produces | Consumes |
|---|---|---|---|---|
| 1a Model | 1a | `model/` | meshes, `surface.json` | settings |
| 1b Scene | 1b | `godot/`, `results/godot/` | Godot project, timing files, dumps | settings, cases, exports, SOFA playback, `core/` |
| 2a Solver | 2a | `solver/` files `fem_core.py`, `cases.py`, `run_static.py`, `viscoelastic.py`, `run_dynamic.py`, `requirements.txt`; `results/ours/static/`, `results/ours/dynamic/` | "ours" results | settings, meshes, cases, profiles |
| 2b Export + core | 2b | `solver/` files `export.py`, `benchmark.py`, `check_parity.py`; `solver/export/`; `core/`; `results/ours/timing/` | influence-matrix export, C# core, timing, parity | `fem_core.py`, settings, meshes, Godot dumps |
| 3 SOFA | 3 | `sofa/`, `results/sofa/`, `results/sofa_large/` | SOFA results, playback files | settings, meshes, cases, profiles |
| 4 Comparison | 4 | `config/`, `compare/` | settings, profiles, cases, tables, figures, results text | meshes, all `results/` |

Briefs: `model/README.md` (1a), `godot/README.md` (1b), `solver/README.md` (2a and 2b), `sofa/README.md` (3), `compare/README.md` (4).

Rule: write only inside what you own. If you need a change in someone else's output, ask the user to relay it; do not edit it yourself.

## 3. Conventions (non-negotiable)

- **Units:** SI only: metres, newtons, pascals, seconds. All arrays are in metres. Visual exaggeration is allowed only inside Godot, must be labelled on screen, and defaults to x1.
- **Coordinates:** right-handed (x, y, z), ball centre at the origin, **z is the polar axis**: polar angles are measured from +z; the fixed cap is at -z. Godot is right-handed and Y-up, so Godot position = (x, z, -y). Triangles in this contract are counter-clockwise when seen from outside; Godot treats clockwise triangles as front faces, so the scene reverses the index order (both statements are from memory; verify them with the visual acceptance check in `godot/README.md`).
- **Node indexing:** 0-based, equal to the order of nodes in `ball.msh`. A **surface row** is the position of a node inside `surface_nodes`. Everything that is per-surface-node (loads, displacements, `G`) is indexed by surface row.
- **DOF / array layout:** node-major, interleaved (x, y, z). Surface DOF index = `3*row + c` with `c` in {0, 1, 2}. Surface arrays have shape `(n_surface, 3)`. Full-mesh DOF = `3*node + c`.
- **Dtypes:** Python/NumPy results are float64. Binary files meant for the engine are float32, little-endian.
- **Time:** force profiles are sampled every `dt_force` = 0.001 s. All time-series outputs are sampled every `dt_out` = 0.01 s, at t = 0, 0.01, 0.02, ... Both values live in `config/test_settings.json`.
- **Names:** mesh level in `coarse | medium | fine`; case ids as in the settings file; amplitude in a file name is written exactly as in the settings file followed by `N` (10 gives `10N`, 2.5 gives `2.5N`).

## 4. What "the same model" means

### 4.1 Mesh, material, constraints
- **Mesh:** linear tetrahedra (P1), topology never changes.
- **Material:** homogeneous, isotropic, constant Poisson's ratio `nu`. Settings give `E_inf`, `E1`, `tau1`. Define `E0 = E_inf + E1`. **Stage 1 uses E = E0** (the instantaneous modulus), so Stage 2 reduces to Stage 1 at t = 0+.
- **Constraints:** all `fixed_nodes` have u = 0 (all three components). A load applied to a fixed node produces no displacement.
- **No body force:** gravity and other body forces are not modelled. Every implementation, including the SOFA scene, runs with gravity switched off.

### 4.2 Loads and cases
- A **load** is a force vector (fx, fy, fz) in newtons applied to one surface row. A **load set** is any collection of loads on distinct rows.
- A **case** (`config/cases/<level>/<case_id>.json`, section 6.4) is a load set with a fixed spatial pattern `p`, normalized so that the sum of the Euclidean norms of its nodal forces is 1. The test amplitude `a` (in newtons, from the settings) scales it: the applied load set is `a * p`, so `a` equals the sum of the nodal force magnitudes.
- Every role uses the case files. Nobody turns a contact description into nodal forces on their own, because that is where mismatches come from.
- **Distributed loads only for tests.** In continuum elasticity a point load gives a displacement that is unbounded under the load, so the finite-element displacement at a single loaded node does not converge with mesh refinement. Physical test cases spread the load over at least `cases.min_nodes` nodes with area weights. The application core nevertheless accepts any load set.

### 4.3 Influence matrix and Stage 1
- Linear problem: `K(E0, nu) u = F`, with fixed DOFs removed.
- The **surface influence matrix** `G` has shape `(3*n_surface, 3*n_surface)`. Column `3*j + b` is the displacement of all surface DOFs for a 1 N load in direction `b` at surface row `j` (all other loads zero), at modulus E0. Columns of fixed rows are zero, and so are rows of fixed rows. `G` is symmetric.
- For any load set with surface-DOF load vector `F_s`: `u_s = G F_s`. Stage 1 result for case `p` and amplitude `a`: `u = a * G p`.

### 4.4 Runtime evaluation (ours)
- The core receives, per step, a sparse list of loads `(row, fx, fy, fz)` and computes `u = sum over nonzero load components of f * G[:, 3*row + c]`. Cost: (number of nonzero load components) x `3*n_surface` multiply-adds. There is no matrix factorization and no solve at runtime.
- The engine writes vertex positions `rest + gain * u` once per rendered frame.
- The application-side input interface is `IForceInput` (in `godot/`): it returns the current load set. This phase has a case-based implementation (case + amplitude from a slider or a profile CSV). The future VR controller will be another implementation; nothing in `core/` depends on it.

### 4.5 Stage 2 (conditional)
For each force component `d` with history `f_d(t)`, an effective force follows the standard linear solid:
`p' = (f - (E_inf/E0) * p) / tau1`,  `f_eff = f + (E1/E0) * p`.
Exact update for a force that is linear within a step `dt` (with `a = (E_inf/(E0*tau1))*dt`, `ea = exp(-a)`, `df = f_next - f_prev`):
`p_next = ea*p + (E0/E_inf) * ( f_prev*(1 - ea) + df*(1 - (1 - ea)/a) )`,  `f_eff_next = f_next + (E1/E0)*p_next`.
Then `u(t) = G f_eff(t)`. State is kept per force component, and it continues to relax after a load is removed. This is valid because `nu` is constant and `G` is evaluated at E0 (correspondence principle). It is derived by superposition from a scalar form that was checked numerically (section 9); the Stage 2 tests (`solver/README.md`) check the vector form. Checks: `f_eff(0+) = f`; under constant force `f_eff -> f*E0/E_inf`.

### 4.6 Validity limits
- Small strain: the linear model is intended for displacements below `validity.max_disp_fraction_of_radius` times the radius (0.10 in the settings). This is a rule of thumb, not a derived error bound. Test points beyond it stay in the test list and are flagged in every output that reports them; the nonlinear check samples amplitudes around the limit (section 7) to show where the departure begins.
- Loads act on the reference surface positions; the contact is not updated as the surface moves.
- No rigid-body motion: the ball is fixed at the -z cap.
- The comparison validates our implementation against SOFA on the same linear problem. It does not validate the model against real rubber; the material values are chosen representative values, not fitted data.

## 5. Project layout

```
DeformGrip/
  CONTRACT.md
  README.md
  config/                [4]    test_settings.json, profiles/<profile_id>.csv, cases/<level>/<case_id>.json
  model/<level>/         [1a]   ball.msh, surface.json            (+ model/README.md)
  solver/                [2a,2b] Python code                      (+ solver/README.md)
    export/<level>/      [2b]   G.bin (not committed), G.meta.json
  core/                  [2b]   plain C# library: src/, tests/
  sofa/                  [3]    scene + run scripts               (+ sofa/README.md)
  godot/                 [1b]   Godot 4 .NET project              (+ godot/README.md)
  results/
    ours/{static,dynamic,timing}/            [2a; 2b writes timing]
    sofa/{static,dynamic,timing,playback}/   [3]
    sofa_large/static/                       [3]  (optional)
    godot/{timing,dumps}/                    [1b]
  compare/               [4]    make_profiles.py, make_cases.py, compare.py, out/   (+ compare/README.md)
```

`G.bin` files are listed in `.gitignore`: they are regenerated with `python solver/export.py`. Everything else listed above is committed.

## 6. File formats

### 6.1 `config/test_settings.json` (owner: Role 4)
Starting values; Role 4 finalizes them at M0 and freezes them after M1. Everyone reads values from this file instead of hard-coding them.

```json
{
  "_notes": "Owner: Role 4. Starting values. Finalize at M0, freeze after M1.",
  "geometry": {"radius_m": 0.035, "fixed_cap_deg": 30},
  "validity": {"max_disp_fraction_of_radius": 0.10},
  "mesh_levels": {"coarse": 800, "medium": 3000, "fine": 10000},
  "material": {"nu": 0.35, "E_inf_Pa": 300000, "E1_Pa": 200000, "tau1_s": 0.5,
               "density_kg_m3": 1000},
  "timing": {"dt_force_s": 0.001, "dt_out_s": 0.01},
  "static": {"forces_N": [1, 2, 5, 10, 20],
             "nonlinear_check_forces_N": [2, 5, 10, 20, 50, 100],
             "playback_table_forces_N": [0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20]},
  "cases": {
    "min_nodes": 5,
    "list": {
      "cap_small":    {"type": "cap", "polar_deg": 0,   "azimuth_deg": 0,   "half_angle_deg": 20,  "direction": "inward_normal"},
      "cap_medium":   {"type": "cap", "polar_deg": 0,   "azimuth_deg": 0,   "half_angle_deg": 30,  "direction": "inward_normal"},
      "cap_large":    {"type": "cap", "polar_deg": 0,   "azimuth_deg": 0,   "half_angle_deg": 40,  "direction": "inward_normal"},
      "cap_shear":    {"type": "cap", "polar_deg": 0,   "azimuth_deg": 0,   "half_angle_deg": 30,  "direction": [1, 0, 0]},
      "cap_oblique":  {"type": "cap", "polar_deg": 0,   "azimuth_deg": 0,   "half_angle_deg": 30,  "direction": [0.5, 0, -0.866]},
      "cap_side":     {"type": "cap", "polar_deg": 90,  "azimuth_deg": 0,   "half_angle_deg": 30,  "direction": "inward_normal"},
      "cap_lower":    {"type": "cap", "polar_deg": 120, "azimuth_deg": 180, "half_angle_deg": 25,  "direction": "inward_normal"},
      "pressure_all": {"type": "cap", "polar_deg": 0,   "azimuth_deg": 0,   "half_angle_deg": 180, "direction": "inward_normal"},
      "random_a":     {"type": "random", "seed": 1, "n_nodes": 40},
      "random_b":     {"type": "random", "seed": 2, "n_nodes": 200}
    }
  },
  "timing_cases": ["cap_medium", "pressure_all"],
  "profiles": {
    "slow_squeeze": {"ramp_s": 3.0,  "peak_N": 10, "hold_s": 3.0, "release_s": 1.0,  "tail_s": 1.0},
    "rapid_press":  {"ramp_s": 0.05, "peak_N": 10, "hold_s": 2.0, "release_s": 0.05, "tail_s": 1.0}
  },
  "stage2": {"enabled": false, "model": "sls", "levels": ["medium"], "cases": ["cap_medium"]},
  "targets": {"rel_L2_max": 0.05, "frame_ms_max": 16.7}
}
```
- `mesh_levels` values are target node counts (accept +-30%). `fixed_cap_deg` is the polar half-angle from the -z pole.
- `validity.max_disp_fraction_of_radius` is the largest displacement, as a fraction of `radius_m`, up to which the linear model is considered valid (section 4.6). Nobody hard-codes it. `nonlinear_check_forces_N` brackets the point where the most compliant physical cases (shear, oblique) reach it.
- Case types: `cap` selects surface nodes whose direction `x/|x|` lies within `half_angle_deg` of the centre direction given by `polar_deg` (from +z) and `azimuth_deg` (from +x towards +y), and weights them by lumped surface area (`surface_areas`, normalized to sum 1). `direction` is `"inward_normal"` (per node `-x/|x|`) or a global 3-vector (normalized by the generator). `random` picks `n_nodes` distinct surface rows with a seeded generator and gives each a random 3D direction and a random positive weight, normalized to sum 1. Random cases are numerical tests of the arbitrary-load path (`"physical": false`): they are excluded from the small-strain rule, the physical figures and the playback files.
- A profile is: linear ramp from 0 to `peak_N` over `ramp_s`, hold for `hold_s`, linear release to 0 over `release_s`, then `tail_s` seconds at zero.
- `timing_cases` are the cases for which timing files are written: a typical case and the worst case in which every surface node is loaded.
- The targets are our own project targets, not values from the literature.

### 6.2 `config/profiles/<profile_id>.csv` (owner: Role 4)
Header `t,f_N`, one row every `dt_force` from t = 0 to the end of the tail. Everyone replays these files; nobody re-implements the profile formulas. A profile scales a case: the load set at time `t` is `f_N(t) * p`.

### 6.3 `model/<level>/ball.msh` and `surface.json` (owner: Role 1a)
- `ball.msh`: Gmsh MSH **2.2 ASCII**; linear tetrahedra (element type 4) plus optional surface triangles (type 2); node tags contiguous 1..N; no unused nodes.
- `surface.json`:

```json
{
  "mesh_level": "medium", "radius_m": 0.035, "n_nodes": 0, "n_tets": 0, "n_surface": 0,
  "fixed_nodes": [0],
  "surface_nodes": [0],
  "surface_positions": [[0.0, 0.0, 0.0]],
  "surface_areas": [0.0],
  "surface_triangles": [[0, 1, 2]]
}
```
`surface_nodes` is sorted ascending (node indices). `surface_positions[k]` is the rest position of `surface_nodes[k]`. `surface_areas[k]` is one third of the summed area of the surface triangles that contain row `k`. `surface_triangles` hold **surface rows** (not node indices), counter-clockwise when seen from outside. `fixed_nodes` are node indices; all of them are surface nodes.

### 6.4 `config/cases/<level>/<case_id>.json` (owner: Role 4, generated by `compare/make_cases.py`)

```json
{
  "case_id": "cap_medium", "mesh_level": "medium", "n_surface": 0, "physical": true,
  "spec": {"type": "cap", "polar_deg": 0, "azimuth_deg": 0, "half_angle_deg": 30, "direction": "inward_normal"},
  "loads": [{"row": 0, "f": [0.0, 0.0, -0.01]}]
}
```
`loads` is the pattern `p` of section 4.2: rows are distinct and ascending, and the sum of the norms of all `f` is 1 (to 1e-12). `spec` is a copy of the settings entry.

### 6.5 Influence-matrix export (owner: Role 2b)
`solver/export/<level>/G.bin`: raw float32 little-endian, column-major, `3*n_surface` rows by `3*n_surface` columns: element (r, c) is at flat index `c * 3*n_surface + r`. Row/column index = `3*row + comp` (section 3). Units: metres per newton at modulus E0. Size: `4 * (3*n_surface)^2` bytes.
`solver/export/<level>/G.meta.json`: `{"mesh_level", "n_surface", "units": "m per N", "E0_Pa", "nu", "E_inf_Pa", "E1_Pa", "tau1_s", "layout": "float32 LE, column-major, index = col*3*n_surface + row"}`.

### 6.6 Result files (owner: whoever produces that source)
Sources: `ours`, `sofa` (small-strain reference), `sofa_large` (optional corotational run), `godot`.
- Static: `results/<source>/static/<level>_<case>_<F>N.npy`, float64, shape `(n_surface, 3)`, metres, for every case in the settings and every amplitude in `static.forces_N`.
- Dynamic (Stage 2 only): `results/<source>/dynamic/<level>_<case>_<profile>.npz` with keys `t` `(n_t,)`, `f` `(n_t,)` (the profile amplitude), `u` `(n_t, n_surface, 3)`, sampled at `dt_out`.
- Timing: `results/<source>/timing/<level>_<case>.json` for the `timing_cases`:
  `{"hardware": "...", "software": "...", "measurements": [{"what_is_timed": "...", "n_samples": 0, "ms_mean": 0, "ms_p95": 0, "ms_max": 0}]}` plus optional extra fields such as `precompute_s` and `export_bytes`. Each measurement needs its own `what_is_timed`, because the sources time different things (ours: full solve, back-substitution, `G` apply; SOFA: one static solve; Godot: core update, whole frame).

### 6.7 Playback and dumps
- SOFA playback (SOFA to Godot): `results/sofa/playback/<level>_<case>_forcetable.bin` + `.json` for every physical case: float32 `[n_frames, n_surface, 3]` plus `{"type": "force_table", "case_id": "...", "forces_N": [...], "n_surface": 0}`. Godot picks the current amplitude and interpolates linearly between frames. (A `time_series` type may be added for Stage 2.)
- Godot dumps: `results/godot/dumps/ours_<level>_<case>_<profile>.bin`, float32 `[n_t, n_surface, 3]` at `dt_out`, written while replaying a profile in "ours" mode.

## 7. Stages, required tests and metrics

- **Stage 1 (mandatory):** static tests: every level x case x amplitude in `static.forces_N`, ours versus `sofa` (small-strain). Points whose largest displacement exceeds the validity limit stay in the tests and are flagged (section 4.6). Timing for every level and each `timing_cases` entry.
- **Nonlinear check (optional):** `sofa_large` versus ours at `static.nonlinear_check_forces_N`, for physical cases, so that the report shows where the linear model starts to depart from the corotational one, not only that it has.
- **Stage 2 (only after the Stage 1 comparison table exists):** dynamic tests with both profiles for the levels and cases in `stage2`. See open item O1.
- **Metrics** (Role 4 implements them; everyone may use them to self-check). With `u_ref` the SOFA small-strain result, over surface nodes:
  - `rel_L2 = ||u - u_ref||_2 / ||u_ref||_2` (flattened `(n_surface*3,)`).
  - `rel_max = max_i ||u_i - u_ref_i|| / max_i ||u_ref_i||` (per-node Euclidean norms).
  - `peak`: the row `i*` with the largest `||u_ref_i||`; compare `||u_i*||` of ours and SOFA.
- **What to expect:** ours and SOFA(small) solve the same linear P1 problem on the same mesh with the same load set, so the difference should be near solver tolerance (rel_L2 around 1e-4 or lower). The 5% target is a loose bound. A result above about 1e-2 means a setup mismatch (units, row order, fixed set, load set, modulus), not "physics".

## 8. Milestones (order, not dates)

- **M0:** Role 4 delivers `test_settings.json` and profiles; Role 1a delivers a stub `ball.msh` + `surface.json` for one level (the schema is what matters); Role 4 generates stub cases from it.
- **M1:** real meshes for all 3 levels (1a); cases for all levels (4); static results from ours (2a) and SOFA (3); exports (2b). Settings are frozen after M1.
- **M2:** Stage 1 comparison table (4); Godot scene runs "ours" live and SOFA force-table playback (1b + 2b core).
- **M3:** report figures, tables and results text (4); timing files from all sources.
- **M4:** Stage 2, only if M3 is done.

## 9. Decisions made, sanity numbers

**Decided:** Python + scikit-fem for the solver; Gmsh for meshes; SOFA for the reference; Godot 4 (.NET edition, C#) for the scene, version pinned at kickoff in `godot/VERSION.txt`; arbitrary 3D nodal loads through the precomputed influence matrix; no physical sensor or controller in this repo; Stage 2 only if time allows; deliverable is a results report.

**Sanity numbers from a prototype at authoring time** (scikit-fem 12.0.2, scipy 1.18.1, numpy 2.5.3, meshio 5.3.5; built-in `MeshTet.init_ball`, not Gmsh; radius 0.035 m, E0 = 500 kPa, nu = 0.35; a single axisymmetric cap at the +z pole with inward normals). Informal values, not reference data:
- ~830 nodes: factorization ~65 ms, one back-substitution 1-2 ms.
- Displacement under the cap centre ~0.08-0.2 mm per newton, so 10 N gives about 1-2 mm (roughly 3-6% of the radius). Shear and oblique cases have not been tried.
- scikit-fem's DOF layout was node-major interleaved (`nodal_dofs[:, i] == 3i..3i+2`) in that version. Assert it, do not assume it.
- The scalar Stage 2 update matched an independent fine-step RK4 solution of the same ODE to about 1e-11.

**Measured in one prototype run (single core, not a benchmark):** the committed meshes have `n_surface` = 456 / 1220 / 2977 for coarse / medium / fine, so `G.bin` is 7 / 54 / 319 MB in float32 (`36*n_surface^2` bytes). Building `G` needs one back-substitution per free surface DOF: about 0.5 / 3.4 / 26 ms per column, so roughly 1 / 12 / 213 s per level after a factorization of 0.06 / 0.7 / 12.5 s. Per-frame cost in the engine is not measured.

## 10. Open items

- **O1 (Stage 2 reference):** it is not yet known whether the installed SOFA has a viscoelastic component that reproduces the standard linear solid. Fallback: Kelvin-Voigt via SOFA Rayleigh stiffness damping, mirrored in ours as a first-order lag (`beta*f_eff' + f_eff = f`, with `beta` equal to the Rayleigh stiffness coefficient). Roles 3 and 4 decide after M2 and record it in `stage2.model` (`"sls"` or `"kv"`). The submitted methodology names the Prony/standard-linear-solid form.
- **O2:** the three `ball.msh` files load in scikit-fem: `solver/fem_core.py` checks that counts and every node coordinate equal the file as read by meshio and `surface.json`, and that the DOF layout is node-major. Loading the same files in SOFA is **not yet verified**; the remaining integration test is: the same `ball.msh` loads in SOFA with identical node counts and total tet volume.
- **O3:** all numeric values in section 6.1 are starting points owned by Role 4. In particular the smallest cap may hold fewer than `min_nodes` nodes on the coarse mesh; Role 4 enlarges it in that case.
- **O4:** SOFA component names and static-solver availability vary by SOFA version; verify them in the installed version instead of trusting names in this document. Whether `ConstantForceField` accepts arbitrary per-node 3D forces for all case types is expected but unverified.
- **O5:** the Godot version, the coordinate map and triangle winding (section 3), and the mesh-update method are untested; 1b verifies and records them.
- **O6:** the per-component Stage 2 form (section 4.5) is untested against SOFA.
