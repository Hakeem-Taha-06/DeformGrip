# Role 4: Comparison, Test Settings and Cases

> One person. Read `../CONTRACT.md` first; it is authoritative. If this file contradicts it, tell the user.

## Purpose
You decide **what gets tested** (how hard to press, how fast, where on the ball, in which direction, how soft the ball is, how fine the mesh is) so that everyone measures the same things. Later you collect the numbers from our solver and SOFA, compare them, and turn them into the tables and figures of the results report. Everyone else waits for your settings file and your case files, so those come first.

## Inputs
All `results/` folders; `model/<level>/surface.json`; 2a's convergence numbers.

## Deliverables
- `config/test_settings.json`, `config/profiles/<profile_id>.csv` and `config/cases/<level>/<case_id>.json` (first, at M0).
- `compare/make_profiles.py`, `compare/make_cases.py`, `compare/compare.py`.
- `compare/out/`: `summary_stage1.csv`, figures (PNG), timing table, `results_section.md`.
- Stage 2 only: dynamic comparison figures.

## Tasks (in order)

### Task 1: settings, profiles and cases (M0, do this first)
1. Start from the values in CONTRACT 6.1. Adjust them using these rules, and write the reason for each change in the `_notes` field of the JSON:
   - **Validity flag:** `validity.max_disp_fraction_of_radius` (0.10) is a rule of thumb, not a derived error bound. Role 2a's first results (E0 = 500 kPa, fine mesh) put the largest displacement at 20 N below 7% of the radius for the axial caps (`cap_small`, `cap_medium`, `cap_large`) but at 24 to 68% for `cap_lower`, `cap_side`, `cap_oblique` and `cap_shear`; the limit is reached near 8.4, 6.9, 5.8 and 2.9 N for those four. All amplitudes stay in the test list; points beyond the limit are flagged (Task 2), and the nonlinear check samples amplitudes around the limit. The ours-versus-SOFA(small) comparison is not affected, because both solve the same linear problem.
   - **Node rule:** every case must contain at least `cases.min_nodes` nodes at every mesh level; enlarge a cap if it does not (ask 1a for actual surface node counts).
   - **Material:** the values are chosen representative values, not fitted data. Keep it that way in anything you write.
   - **Targets:** `rel_L2_max` and `frame_ms_max` are our own project targets.
2. Write `compare/make_profiles.py` that reads the `profiles` section and writes `config/profiles/<id>.csv` (`t,f_N`, every `dt_force`).
3. Write `compare/make_cases.py` that reads the settings and each `model/<level>/surface.json` and writes `config/cases/<level>/<case_id>.json` (CONTRACT 4.2 and 6.4): node selection, area weighting, direction, normalization to a unit sum of force norms, and the `min_nodes` assertion. Run both scripts and commit the outputs.
4. Freeze the settings after M1. A later change must be announced to all roles, because it invalidates their results.

### Task 2: Stage 1 comparison (M2)
1. `compare.py` loads `results/ours/static/*` and `results/sofa/static/*`, aligns them by file name, and computes `rel_L2`, `rel_max` and the `peak` comparison (CONTRACT 7). Output `compare/out/summary_stage1.csv` with one row per (level, case, force).
2. Add mesh convergence (largest displacement per newton of each physical case, coarse to fine) from 2a's numbers or from the result files.
3. Pass/fail against `targets.rel_L2_max`. Also report the actual values, since they should be far below the target (CONTRACT 7).
4. Add a `beyond_validity` column to `summary_stage1.csv`: true when the largest surface displacement of the SOFA small-strain result exceeds `validity.max_disp_fraction_of_radius` times `radius_m`. These points stay in the table and are marked in the figures.

### Task 3: performance (M3)
Build one table per mesh level from the timing files, with these columns kept separate because they measure different things:
- one-off precompute time (building `G`) and export size (2b);
- ours per-frame cost: NumPy `G` apply (2b) and Godot core update and whole-frame time (1b), for the typical case and for the worst case in which every surface node is loaded;
- the cost of a normal solve per frame for comparison (2b benchmark items (i) and (ii));
- SOFA per-solve time (3).
Compare the whole-frame Godot time with `targets.frame_ms_max`. State honestly that ours does little work per frame because the response is precomputed, and that this is the trade-off of precomputation: one fixed mesh and material, a linear model, and an influence matrix whose size grows with the square of the surface node count.

### Task 4: figures and text (M3)
Figures (matplotlib, saved as PNG at 200 dpi): (a) `rel_L2` versus force for each case and level; (b) largest displacement versus force, ours versus SOFA, with the validity limit drawn; (c) timing by mesh level with the frame budget line; (d) mesh convergence. Optional: (e) nonlinear check, ours versus `sofa_large` at `static.nonlinear_check_forces_N`, with the validity limit marked, showing where the linear model starts to depart; (f) Stage 2 creep or relaxation curves.
Write `compare/out/results_section.md`: a short paragraph per figure and table, using only measured numbers. Do not claim clinical efficacy, and do not call the comparison a validation of the physics (see the next paragraph). Random cases appear only in the table and in the agreement statistics, not in the physical figures.

### What the comparison shows, and what it does not
Ours and SOFA(small) solve the same linear P1 problem on the same mesh with the same load sets, so a near-zero difference shows that our precomputation, export and runtime pipeline is **implemented correctly**, including for arbitrary 3D load sets (the random cases test that directly). It does not show that the model matches real rubber; no physical experiment was done, and the material values are chosen representative values. Agreement on the finite list of cases supports, but does not prove, correctness for every possible load set; superposition is what extends it. The nonlinear check (if run) shows where the linear assumption stops being reasonable. Write the results section with this scope.

### Task 5: Stage 2 (M4, only if enabled)
Compare ours (`stage2.model`) against `sofa/dynamic`: the creep/relaxation curve of the `peak` node under `slow_squeeze` and `rapid_press`. The rapid press is where the quasi-static model is expected to miss the inertial peak that SOFA's dynamic run includes; report that as an expected limitation, not as a bug.

## Mismatch checklist (use when Stage 1 `rel_L2` is above ~1e-2)
Check in this order, since the earlier items are the most common:
1. Row order or index base differs (0-based vs 1-based; surface rows vs node indices).
2. Units differ (mm vs m, kPa vs Pa).
3. Different modulus: one side used `E_inf` instead of `E0 = E_inf + E1`.
4. Different fixed set.
5. Different load set: a case file not used by both sides, sign or direction, amplitude convention (the sum of force norms equals the amplitude).
6. Different `nu`.
7. SOFA not converged (tighten the solver tolerance) or SOFA `method` is not `"small"`.
8. Ours: wrong `G` layout (row-major read of a column-major file) or wrong column index (`3*row + c`).
Quick discriminator: if the ratio `u_ours / u_sofa` is the same constant at every node, the cause is a scalar (modulus, units, force magnitude); if it varies from node to node, the cause is geometric (order, fixed set, load distribution).

## Acceptance checks
- `config/test_settings.json` parses, contains every key shown in CONTRACT 6.1, and each profile CSV exists with the expected duration and peak.
- Every case file exists for every level, validates against CONTRACT 6.4, and has at least `min_nodes` loaded nodes.
- `compare.py` runs from a clean checkout and regenerates `compare/out/` with no manual steps.
- `summary_stage1.csv` has one row per (level, case, force) and no missing cells.
- `summary_stage1.csv` has a `beyond_validity` column, and the results text names the cases and amplitudes that exceed the validity limit.
- Every number in `results_section.md` can be traced to a row in a CSV or JSON file.
- The results text states the scope given above.

## Not your job
Writing solvers, meshes, SOFA scenes or engine code. If a result looks wrong, use the checklist above and report to the owner of that source via the user; do not edit their files.

## Change to the settings (made by Role 2a after the first solver results)
- Added `validity.max_disp_fraction_of_radius` = 0.10 to `config/test_settings.json`; read it instead of hard-coding 10%.
- `static.nonlinear_check_forces_N` changed from `[20, 50, 100]` to `[2, 5, 10, 20, 50, 100]`, so the nonlinear check brackets the limit of the most compliant cases (about 2.9 N for `cap_shear`).
- The `_notes` field no longer says "PENDING"; it records the measured numbers above.
- No script needs rerunning: `make_cases.py` and `make_profiles.py` read none of the changed keys, and neither does `model/make_ball.py`.
