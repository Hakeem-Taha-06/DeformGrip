# Role 3: SOFA Reference

> One person. Read `../CONTRACT.md` first; it is authoritative. If this file contradicts it, tell the user.

## Purpose
You run the ready-made simulation (SOFA) on exactly the same ball, with exactly the same fixed part and the same load sets as our own solver, and save the numbers. These numbers are the answer key the comparison role checks us against. You also package results so the Godot scene can show them.

## Inputs
`config/test_settings.json`, `model/<level>/ball.msh`, `model/<level>/surface.json`, `config/cases/<level>/*.json`, `config/profiles/*.csv` (Stage 2 only).

## Deliverables
- `sofa/` scene and run scripts that regenerate everything with one command (`sofa/run_all.py` or equivalent).
- `results/sofa/static/*.npy`, `results/sofa/timing/*.json`, `results/sofa/playback/*`, `results/sofa/VERSION.txt` (SOFA version and how it was installed).
- Optional: `results/sofa_large/static/*.npy`.
- Stage 2 only: `results/sofa/dynamic/*.npz`.

## Tasks (in order)
1. Install SOFA from the official release binaries. Check that the SofaPython3 plugin is present in your download; if it is, script the runs in Python. Record the version in `results/sofa/VERSION.txt`.
2. Build the static scene (component names are as in SOFA 23.06+ and may differ in your version; verify them, see O4):
   - load `model/<level>/ball.msh` with `MeshGmshLoader`; one `MechanicalObject` (Vec3) with all nodes, in file order;
   - `TetrahedronSetTopologyContainer` for the tetrahedra;
   - `TetrahedronFEMForceField` with `method="small"` (linear, small displacement), `youngModulus = E0 = E_inf + E1`, `poissonRatio = nu` from the settings;
   - fixed set: a fixed projective constraint (older versions: `FixedConstraint`) on `fixed_nodes`;
   - load: `ConstantForceField` with `indices = surface_nodes[row]` and forces `a * f` for every load of the case (nothing else may define the load);
   - gravity: set to zero for every scene (the model has no body force, CONTRACT 4.1);
   - solver: SOFA's `StaticSolver` if your version has it, with a direct or tightly converged linear solver (tolerance 1e-10 or better); otherwise implicit Euler run to steady state with enough damping and iterations that displacement stops changing at 1e-10.
3. `run_static.py`: for every level, case and amplitude in `static.forces_N`: run the scene, compute displacement = final position - rest position, take the surface rows (order of `surface_nodes`), save `results/sofa/static/<level>_<case>_<F>N.npy` (float64, `(n_surface, 3)`).
4. **Check against ours before reporting:** once 2a's static results exist, compute `rel_L2` for a few cases (include a `random_*` case and a shear case). Expect about 1e-4 or lower (CONTRACT 7). If it is larger than 1e-2, stop and use the mismatch checklist in `compare/README.md` before doing anything else.
5. Timing: write `results/sofa/timing/<level>_<case>.json` for the `timing_cases`. The `what_is_timed` of each measurement must state exactly what was timed (for a static case: wall-clock of one solve for a fresh load, excluding scene setup), with mean/p95/max over repeated runs and the hardware.
6. **Godot playback files:** for each level and each physical case, run the static scene at every amplitude in `static.playback_table_forces_N` and write `results/sofa/playback/<level>_<case>_forcetable.bin` (float32 `[n_frames, n_surface, 3]`) and the matching `.json` (CONTRACT 6.7).
7. *Optional nonlinear check:* repeat the static runs for the physical cases with `method="large"` (corotational) at `static.nonlinear_check_forces_N` (the amplitudes bracket the validity limit, CONTRACT 4.6). The medium level is enough to start with; add the other levels if time allows. Save to `results/sofa_large/static/`.
8. *Stage 2, only after M2 (open item O1):* first check whether the installed SOFA has a viscoelastic force field or material that reproduces the standard linear solid (E_inf, E1, tau1). If it does not, use Kelvin-Voigt via Rayleigh stiffness damping and tell Role 4 so `stage2.model` can be set to `"kv"`. Run the two profiles with the same mass density and time step, and save `results/sofa/dynamic/<level>_<case>_<profile>.npz` (CONTRACT 6.6). A dynamic SOFA run includes inertia and ours does not; this is intended for the rapid-press comparison (CONTRACT 7).

## Acceptance checks
- `sofa/run_all.py` regenerates every file in `results/sofa/` from `config/` and `model/` with no manual steps.
- Static files exist for all levels x cases x amplitudes; no NaN; `u = 0` at fixed nodes (to 1e-12).
- Linearity: the result at 20 N equals 2x the result at 10 N within 1e-6 relative (the small-strain solver is linear).
- `rel_L2` against ours is below `targets.rel_L2_max` for all static tests, and typically around 1e-4 or lower.
- Playback `.bin` size equals `4 * n_frames * n_surface * 3` bytes and the JSON lists the same forces as the settings.
- `VERSION.txt` and timing files exist with all required fields.

## Not your job
Meshing (1a), the Godot scene (1b), our solver (2), choosing the test values or doing the comparison (4).
