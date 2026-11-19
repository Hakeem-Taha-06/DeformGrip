# ROLE 3: SOFA Reference

> One person. Part A (this section) is your role; Part B (after it) is the shared contract used by all roles. Read both. Part B wins on conflicts.

# PART A: ROLE-SPECIFIC

## A.0 In plain words
You run the ready-made simulation (SOFA) on exactly the same ball, with exactly the same fixed part and the same push as our own solver, and save the numbers. These numbers are the "answer key" the comparison role checks us against. You also package the results so the Unity scene can show them.

## A.1 Inputs
`config/test_settings.json`, `model/<level>/ball.msh`, `model/<level>/regions.json`, `config/profiles/*.csv` (Stage 2 only).

## A.2 Deliverables
- `sofa/` scene and run scripts that regenerate everything with one command (`sofa/run_all.py` or equivalent).
- `results/sofa/static/*.npy`, `results/sofa/timing/*.json`, `results/sofa/playback/*`, `results/sofa/VERSION.txt` (SOFA version and how it was installed).
- Optional: `results/sofa_large/static/*.npy`.
- Stage 2 only: `results/sofa/dynamic/*.npz`.

## A.3 Tasks (in order)
1. Install SOFA from the official release binaries. The SofaUnity README requires SOFA newer than v23.06, so prefer that or newer (this only matters if the optional live Unity link is ever attempted). Check that the SofaPython3 plugin is present in your download; if it is, script the runs in Python. Record the version in `results/sofa/VERSION.txt`.
2. Build the static scene (component names below are as in SOFA 23.06+ and may differ in your version; verify them, see O4):
   - load `model/<level>/ball.msh` with `MeshGmshLoader`; one `MechanicalObject` (Vec3) with all nodes, in file order;
   - `TetrahedronSetTopologyContainer` for the tetrahedra;
   - `TetrahedronFEMForceField` with `method="small"` (linear, small displacement), `youngModulus = E0 = E_inf + E1`, `poissonRatio = nu` from the settings;
   - fixed set: a fixed projective constraint (older versions: `FixedConstraint`) on `fixed_nodes`;
   - load: `ConstantForceField` on the patch `nodes` with forces `f * weights[k] * normals[k]`;
   - solver: SOFA's `StaticSolver` if your version has it, with a direct or tightly converged linear solver (tolerance 1e-10 or better); otherwise implicit Euler run to steady state with enough damping and iterations that displacement stops changing at 1e-10.
3. `run_static.py`: for every level, patch and `static.forces_N`: run the scene, compute displacement = final position - rest position, take the surface rows (order of `surface_nodes`), save `results/sofa/static/<level>_<patch>_<F>N.npy` (float64, `(n_surface, 3)`).
4. **Check against ours before reporting:** once 2a's static results exist, compute `rel_L2` for a few cases. Expect about 1e-4 or lower (B.7). If it is larger than 1e-2, stop and use the mismatch checklist in ROLE_4 (Part A, A.4) before doing anything else.
5. Timing: write `results/sofa/timing/<level>_<patch>.json`. The `what_is_timed` of each measurement must state exactly what was timed (for a static case: wall-clock of one solve for a fresh load, excluding scene setup), with mean/p95/max over repeated runs and the hardware.
6. **Unity playback files:** for each level and patch, run the static scene at every force in `static.playback_table_forces_N` and write `results/sofa/playback/<level>_<patch>_forcetable.bin` (float32 `[n_frames, n_surface, 3]`) and the matching `.json` (B.6.5). This lets 1b show SOFA's deformation as a function of force without needing SOFA inside Unity.
7. *Optional nonlinear check:* repeat the static runs with `method="large"` (corotational) at `static.nonlinear_check_forces_N`; save to `results/sofa_large/static/`.
8. *Stage 2, only after M2 (open item O1):* first check whether the installed SOFA has a viscoelastic force field or material that reproduces the standard linear solid (E_inf, E1, tau1). If it does not, use Kelvin-Voigt via Rayleigh stiffness damping and tell Role 4 so `stage2.model` can be set to `"kv"`. Run the two profiles with the same mass density and time step, and save `results/sofa/dynamic/<level>_<patch>_<profile>.npz` (B.6.5). Be aware that a dynamic SOFA run includes inertia, and ours does not (this is intended for the rapid-press comparison, see B.7).
9. *Optional, last, time-boxed:* if everything else is complete, you may try the SofaUnity live link for a demo (B.9, README facts). It must never replace tasks 1-8, and nothing else may depend on it.

## A.4 Acceptance checks
- `sofa/run_all.py` regenerates every file in `results/sofa/` from `config/` and `model/` with no manual steps.
- Static files exist for all levels x patches x forces; no NaN; `u = 0` at fixed nodes (to 1e-12).
- Linearity: the result at 20 N equals 2x the result at 10 N within 1e-6 relative (the small-strain solver is linear).
- `rel_L2` against ours is below `targets.rel_L2_max` for all static tests, and typically around 1e-4 or lower.
- Playback `.bin` size equals `4 * n_frames * n_surface * 3` bytes and the JSON lists the same forces as the settings.
- `VERSION.txt` and timing files exist with all required fields.

## A.5 Not your job
Meshing (1a), the Unity scene (1b), our solver (2), choosing the test values or doing the comparison (4).

---

# PART B: SHARED CONTRACT (identical in all four role files)

This part is authoritative. If your role section (Part A) contradicts Part B, Part B wins: do not silently pick one, tell the user about the contradiction. If anything in Part B must change, the user must make the same edit in all four files, so ask first rather than working around it.

## B.1 The project in one paragraph

We simulate squeezing a soft rubber ball (a grip-rehabilitation game object) in real time. The model is small-strain linear elasticity on a tetrahedral mesh (**Stage 1, mandatory**), optionally extended with one-term Prony-series viscoelasticity (**Stage 2, only if time allows**). We build two implementations of the same model: **ours** (offline precomputation in Python with scikit-fem, runtime in Unity/C#) and a **SOFA reference** (SOFA Framework, run offline). The final deliverable is a results report comparing the accuracy and speed of the two. There is no physical sensor yet: force input is a replayed profile or a manual slider. Clinical claims are out of scope.

Team constraints: 6 people, each with roughly 4-5 hours per week, and not everyone is comfortable with Unity. Choose the simplest option that satisfies this contract. Do not add features that are not listed.

## B.2 Roles and ownership

| Role file | People | Owns (write only here) | Produces | Consumes |
|---|---|---|---|---|
| ROLE_1_model_and_scene.md | 1a Model, 1b Unity scene | `model/`, `unity/` (except `unity/Assets/Scripts/Core/`), `results/unity/` | meshes + regions, Unity project | settings, export files, results |
| ROLE_2_solver.md | 2a Solver core, 2b Precompute/export + C# core | `solver/`, `unity/Assets/Scripts/Core/`, `results/ours/` | "ours" results, exported vectors, C# core classes | settings, meshes + regions |
| ROLE_3_sofa_reference.md | 3 SOFA reference | `sofa/`, `results/sofa/`, `results/sofa_large/` | SOFA reference results + Unity playback files | settings, meshes + regions, profiles |
| ROLE_4_comparison.md | 4 Comparison + test settings | `config/`, `compare/` | settings, profiles, tables, figures, results text | all `results/` |

Rule: write only inside the folders you own. If you need a change in someone else's output, ask the user to relay it; do not edit it yourself.

## B.3 Conventions (non-negotiable)

- **Units:** SI only: metres, newtons, pascals, seconds. All arrays are in metres. Visual exaggeration is allowed only inside Unity, must be labelled on screen, and defaults to x1.
- **Coordinates:** right-handed (x, y, z), ball centre at the origin, **z is the squeeze axis**. Load patch at the +z pole, fixed cap at the -z pole. Unity is left-handed and Y-up, so Unity position = (x, z, y). If the ball looks inside-out in Unity, reverse the triangle index order.
- **Node indexing:** 0-based, equal to the order of nodes in `ball.msh`. A "surface row" is the position of a node inside `surface_nodes`.
- **DOF / array layout:** node-major, interleaved (x, y, z), so DOF `3*i + c`. Surface arrays have shape `(n_surface, 3)`.
- **Dtypes:** Python/NumPy results are float64. Binary files meant for Unity are float32, little-endian.
- **Time:** force profiles are sampled every `dt_force` = 0.001 s. All time-series outputs are sampled every `dt_out` = 0.01 s, at t = 0, 0.01, 0.02, ... Both values live in `config/test_settings.json`.
- **Names:** mesh level in `coarse | medium | fine`; patch in `small | medium | large`; force in the file name is written exactly as in the settings file followed by `N` (10 gives `10N`, 2.5 gives `2.5N`).

## B.4 What "the same model" means

- **Mesh:** linear tetrahedra (P1), topology never changes.
- **Material:** homogeneous, isotropic, constant Poisson's ratio `nu`. Settings give `E_inf`, `E1`, `tau1`. Define `E0 = E_inf + E1`. **Stage 1 uses E = E0** (the instantaneous modulus), so Stage 2 reduces to Stage 1 at t = 0+.
- **Boundary conditions:** all fixed nodes have u = 0 (all three components). The load acts only on the nodes of one patch `p`: `F_i = f * w_i * n_i`, where `w_i` are area weights that sum to 1 and `n_i = -x_i/|x_i|` is the unit inward normal. So `f` is the sum of the nodal force magnitudes; the net axial force is slightly smaller (cosine of the node's polar angle). Every role must use the identical rule, which is why `regions.json` stores the weights and normals.
- **Stage 1:** solve `K(E0, nu) u = F`. Because the load pattern of a patch is fixed and only the scalar `f` changes, `u = f * v_p`, where `v_p = K^-1 F_p` is the response to a 1 N load. **v_p is "the precomputed response"**: one solve per patch and mesh level.
- **Stage 2 (conditional):** `u(t) = u~(t) * v_p`, where the scalar `u~` follows the standard linear solid driven by the force `f(t)`:
  `p' = (f - (E_inf/E0) * p) / tau1`,  `u~ = f + (E1/E0) * p`.
  Exact update for a force that is linear within a step `dt` (with `a = (E_inf/(E0*tau1))*dt`, `ea = exp(-a)`, `df = f_next - f_prev`):
  `p_next = ea*p + (E0/E_inf) * ( f_prev*(1 - ea) + df*(1 - (1 - ea)/a) )`,  `u~_next = f_next + (E1/E0)*p_next`.
  This is valid because `nu` is constant and the load pattern is fixed (correspondence principle). Checks: `u~(0+) = f`; under constant force `u~ -> f*E0/E_inf`.
- **Linear-validity limit:** the linear model is trusted only while the apex displacement stays below about 10% of the radius. Larger loads are used only in the optional "nonlinear check" (B.7).

## B.5 Project layout

```
project/
  config/                 [4]    test_settings.json, profiles/<profile_id>.csv
  model/<level>/          [1a]   ball.msh, regions.json
  solver/                 [2a,2b] code + export/<level>/<patch>.vec.bin, .meta.json
  sofa/                   [3]    scene + run scripts
  unity/                  [1b]   Unity project; Assets/Scripts/Core/ is [2b]
  results/
    ours/{static,dynamic,timing}/            [2a, 2b writes timing]
    sofa/{static,dynamic,timing,playback}/   [3]
    sofa_large/static/                       [3]  (optional)
    unity/{timing,dumps}/                    [1b]
  compare/                [4]    compare.py, out/
```

## B.6 File formats

### B.6.1 `config/test_settings.json` (owner: Role 4)
Starting values below; Role 4 finalizes them at M0 and freezes them after M1. Everyone reads values from this file instead of hard-coding them.

```json
{
  "_notes": "Owner: Role 4. Starting values. Finalize at M0, freeze after M1.",
  "geometry": {"radius_m": 0.035, "fixed_cap_deg": 30,
               "patches_deg": {"small": 15, "medium": 25, "large": 35}},
  "mesh_levels": {"coarse": 800, "medium": 3000, "fine": 10000},
  "material": {"nu": 0.35, "E_inf_Pa": 300000, "E1_Pa": 200000, "tau1_s": 0.5,
               "density_kg_m3": 1000},
  "timing": {"dt_force_s": 0.001, "dt_out_s": 0.01},
  "static": {"forces_N": [1, 2, 5, 10, 20],
             "nonlinear_check_forces_N": [20, 50, 100],
             "playback_table_forces_N": [0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20]},
  "profiles": {
    "slow_squeeze": {"ramp_s": 3.0,  "peak_N": 10, "hold_s": 3.0, "release_s": 1.0,  "tail_s": 1.0},
    "rapid_press":  {"ramp_s": 0.05, "peak_N": 10, "hold_s": 2.0, "release_s": 0.05, "tail_s": 1.0}
  },
  "stage2": {"enabled": false, "model": "sls", "levels": ["medium"], "patches": ["medium"]},
  "targets": {"rel_L2_max": 0.05, "frame_ms_max": 16.7}
}
```
`mesh_levels` values are target node counts (accept +-30%). `fixed_cap_deg` and `patches_deg` are polar half-angles measured from the -z and +z poles. A profile is: linear ramp from 0 to `peak_N` over `ramp_s`, hold for `hold_s`, linear release to 0 over `release_s`, then `tail_s` seconds at zero. The targets are our own project targets, not values from the literature.

### B.6.2 `config/profiles/<profile_id>.csv` (owner: Role 4)
Header `t,f_N`, one row every `dt_force` from t = 0 to the end of the tail. Everyone replays these files; nobody re-implements the profile formulas.

### B.6.3 `model/<level>/ball.msh` and `regions.json` (owner: Role 1a)
- `ball.msh`: Gmsh MSH **2.2 ASCII**; linear tetrahedra (element type 4) plus optional surface triangles (type 2); node tags contiguous 1..N; no unused nodes.
- `regions.json`:

```json
{
  "mesh_level": "medium", "radius_m": 0.035, "n_nodes": 0, "n_tets": 0,
  "fixed_nodes": [0],
  "surface_nodes": [0],
  "surface_positions": [[0.0, 0.0, 0.0]],
  "surface_triangles": [[0, 1, 2]],
  "patches": {
    "small": {"half_angle_deg": 15, "nodes": [0], "weights": [1.0], "normals": [[0.0, 0.0, -1.0]]}
  }
}
```
`surface_nodes` is sorted ascending. `surface_positions[k]` is the rest position of `surface_nodes[k]`. `surface_triangles` hold node indices (not surface rows), ordered counter-clockwise when seen from outside. Each patch's `nodes`, `weights`, `normals` are aligned. Patches are nested (small inside medium inside large).

### B.6.4 Precomputed export (owner: Role 2b)
`solver/export/<level>/<patch>.vec.bin`: raw float32 little-endian, length `3*n_surface`, row k = displacement in metres of `surface_nodes[k]` for a 1 N load at modulus E0. `solver/export/<level>/<patch>.meta.json`: `{"mesh_level", "patch", "n_surface", "units": "m per N", "E0_Pa", "nu", "E_inf_Pa", "E1_Pa", "tau1_s", "layout": "float32 LE, rows=surface_nodes order, interleaved xyz"}`. Optional stretch file `<patch>.G.bin`: float32 row-major matrix `[3*n_surface, 3*n_patch_nodes]` with columns in patch-node order, x,y,z interleaved.

### B.6.5 Result files (owner: whoever produces that source)
Sources: `ours`, `sofa` (small-strain reference), `sofa_large` (optional corotational run), `unity`.
- Static: `results/<source>/static/<level>_<patch>_<F>N.npy`, float64, shape `(n_surface, 3)`, metres.
- Dynamic: `results/<source>/dynamic/<level>_<patch>_<profile>.npz` with keys `t` `(n_t,)`, `f` `(n_t,)`, `u` `(n_t, n_surface, 3)`, sampled at `dt_out`.
- Timing: `results/<source>/timing/<level>_<patch>.json`:
  `{"hardware": "...", "software": "...", "measurements": [{"what_is_timed": "...", "n_samples": 0, "ms_mean": 0, "ms_p95": 0, "ms_max": 0}]}` plus optional extra fields such as `precompute_s` and `export_bytes`. Each measurement needs its own `what_is_timed`, because the sources time different things (for example ours: scale step, back-substitution, full solve; SOFA: one static solve; Unity: core update, whole frame).
- Unity playback (SOFA to Unity), `results/sofa/playback/<level>_<patch>_forcetable.bin` + `.json`: float32 `[n_frames, n_surface, 3]` plus `{"type": "force_table", "forces_N": [...], "n_surface": 0}`. Unity picks the current force and interpolates linearly between frames. (A `time_series` type may be added for Stage 2.)
- Unity dumps: `results/unity/dumps/ours_<level>_<patch>_<profile>.bin`, float32 `[n_t, n_surface, 3]` at `dt_out`.

## B.7 Stages and required tests

- **Stage 1 (mandatory):** static tests, every level x patch x force in `static.forces_N`, ours versus `sofa` (small-strain). Timing for every level.
- **Nonlinear check (optional):** `sofa_large` versus ours at `static.nonlinear_check_forces_N`, to show where the linear model stops being valid.
- **Stage 2 (only after the Stage 1 comparison table exists):** dynamic tests with both profiles for the levels/patches in `stage2`. See open item O1.
- **Metrics** (Role 4 implements them; everyone may use them to self-check). With `u_ref` the SOFA small-strain result, over surface nodes:
  - `rel_L2 = ||u - u_ref||_2 / ||u_ref||_2` (flattened `(n_surface*3,)`).
  - `rel_max = max_i ||u_i - u_ref_i|| / max_i ||u_ref_i||` (per-node Euclidean norms).
  - `apex`: the surface node closest to the +z pole; compare its `|u_z|`.
- **What to expect:** ours and SOFA(small) solve the same linear P1 problem on the same mesh, so the difference should be near solver tolerance (rel_L2 around 1e-4 or lower). The 5% target is a loose bound. A result above about 1e-2 means a setup mismatch (units, node order, fixed set, load vector, modulus), not "physics". The comparison validates our implementation against SOFA; it does **not** validate the model against real rubber.

## B.8 Milestones (order, not dates)

- **M0:** Role 4 delivers `test_settings.json` and profiles; Role 1a delivers a stub `regions.json` + `ball.msh` for one level (the schema is what matters).
- **M1:** real meshes for all 3 levels (1a); static results from ours (2a) and SOFA (3); exports (2b).
- **M2:** Stage 1 comparison table (4); Unity scene runs "ours" live and SOFA force-table playback (1b + 2b core).
- **M3:** report figures, tables and results text (4); timing files from all sources.
- **M4:** Stage 2, only if M3 is done.

## B.9 Decisions made, sanity numbers, open items

**Decided:** Python + scikit-fem for the solver; Gmsh for meshes; SOFA for the reference; Unity 2021.3 LTS; no physical sensor; Stage 2 only if time allows; deliverable is a results report. The SofaUnity plugin (github.com/InfinyTech3D/SofaUnity) is a reference, **not a dependency**: its README states it needs Unity newer than 2020.3.x (tested on 2020.3.17 and 2021.3.19), SOFA newer than v23.06 with the SofaVerseAPI DLL, was only tested on Windows, and its maintenance status is unclear.

**Sanity numbers from a prototype at authoring time** (scikit-fem 12.0.2, scipy 1.18.1, numpy 2.5.3, meshio 5.3.5; built-in `MeshTet.init_ball`, not Gmsh; radius 0.035 m, E0 = 500 kPa, nu = 0.35). Informal sanity values, not reference data:
- ~830 nodes: factorization ~65 ms, one back-substitution 1-2 ms.
- Apex displacement ~0.08-0.2 mm per newton, so 10 N gives about 1-2 mm (roughly 3-6% of the radius).
- scikit-fem's DOF layout was node-major interleaved (`nodal_dofs[:, i] == 3i..3i+2`) in that version. Assert it, do not assume it.
- Coarse meshes can leave a small patch with 1-5 nodes; hence the rule of at least 5 nodes per patch (Role 1a).
- The Stage 2 update in B.4 matched an independent fine-step RK4 solution of the same ODE to about 1e-11.

**Open items**
- **O1 (Stage 2 reference):** it is not yet known whether the installed SOFA has a viscoelastic component that reproduces the standard linear solid. Fallback: Kelvin-Voigt via SOFA Rayleigh stiffness damping, mirrored in ours as a first-order lag (`beta*u~' + u~ = f`, with `beta` equal to the Rayleigh stiffness coefficient). Roles 3 and 4 decide after M2 and record it in `stage2.model` (`"sls"` or `"kv"`). Note: the submitted methodology names the Prony/standard-linear-solid form.
- **O2:** the Gmsh script and the loading of the produced MSH 2.2 file in SOFA and in scikit-fem were **not executed** when this contract was written (Gmsh could not run in the authoring environment). The first integration test is: the same `ball.msh` loads in both, with identical node counts and total tet volume.
- **O3:** all numeric values in B.6.1 are starting points owned by Role 4.
- **O4:** SOFA component names and static-solver availability vary by SOFA version; verify them in the installed version instead of trusting names in this document.
