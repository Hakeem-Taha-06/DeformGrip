# DeformGrip

Real-time finite element simulation of deformable objects for force-guided hand rehabilitation.

A soft ball deforms in real time under arbitrary 3D forces applied to its surface. The linear elastic response is precomputed offline as a surface influence matrix (Python, scikit-fem); the Godot (C#) runtime only adds up weighted columns of it each frame. A SOFA reference run provides the answer key, and the project deliverable is a report comparing accuracy and speed.

The full specification is in [CONTRACT.md](CONTRACT.md). Read it before your role brief.

## Repository layout

```
CONTRACT.md                    shared specification (authoritative)
config/                [Role 4]   settings, force profiles, test cases
model/                 [Role 1a]  ball meshes + surface data        -> model/README.md
solver/                [Role 2a, 2b] Python solver, export, benchmark -> solver/README.md
  export/                         influence matrices (regenerated, not committed)
core/                  [Role 2b]  plain C# library used by the scene
sofa/                  [Role 3]   SOFA reference                    -> sofa/README.md
godot/                 [Role 1b]  Godot 4 .NET scene                -> godot/README.md
results/                          outputs, one subfolder per source
compare/               [Role 4]   comparison, figures, report text  -> compare/README.md
```

## Roles

| Role | Workspace | Brief |
|---|---|---|
| 1a Model | `model/` | [model/README.md](model/README.md) |
| 1b Scene | `godot/`, `results/godot/` | [godot/README.md](godot/README.md) |
| 2a Solver | `solver/` (solver files), `results/ours/static`, `results/ours/dynamic` | [solver/README.md](solver/README.md) |
| 2b Export + core | `solver/` (export files), `core/`, `results/ours/timing` | [solver/README.md](solver/README.md) |
| 3 SOFA | `sofa/`, `results/sofa/`, `results/sofa_large/` | [sofa/README.md](sofa/README.md) |
| 4 Comparison | `config/`, `compare/` | [compare/README.md](compare/README.md) |

## Data flow

| Producer | Output | Consumers |
|---|---|---|
| 4 | `config/test_settings.json`, `config/profiles/`, `config/cases/` | all |
| 1a | `model/<level>/ball.msh`, `surface.json` | 2a, 2b, 3, 1b, 4 |
| 2a | `results/ours/static/*`, `solver/fem_core.py` | 2b, 3 (self-check), 4 |
| 2b | `solver/export/<level>/G.bin`, `core/`, `results/ours/timing/*` | 1b, 4 |
| 3 | `results/sofa/*`, `results/sofa_large/*` | 1b (playback), 4 |
| 1b | `results/godot/timing/*`, `results/godot/dumps/*` | 2b (parity), 4 |
| 4 | `compare/out/*` | report |

## Final test run (everything except SOFA)

Requirements: Python with `pip install -r solver/requirements.txt`, the .NET 8 SDK, and the Godot version pinned in `godot/VERSION.txt` (.NET edition). `godot/DeformGrip.csproj` builds against the real core (`UseCoreStub` is false).

1. **Influence matrices** (not in git, 7 / 51 / 304 MiB; the fine level takes several minutes): `python solver/export.py`
2. **C# core against Python:** `python solver/check_parity.py` (part 1; it stops with a clear message if a `G.bin` is missing).
3. **Godot dumps** (run from the repository root; `godot` is your Godot .NET executable):
   ```
   godot --headless --path godot -- --dump coarse cap_medium slow_squeeze
   godot --headless --path godot -- --dump medium cap_medium slow_squeeze
   godot --headless --path godot -- --dump fine cap_shear rapid_press
   godot --headless --path godot -- --dump medium pressure_all slow_squeeze
   ```
   Each writes `results/godot/dumps/ours_<level>_<case>_<profile>.bin` (4 to 29 MB; commit only a few). Then run `python solver/check_parity.py` again; part 2 compares the dumps with Python.
4. **Look at the scene** in the Godot editor (F5): the ball must not be inside-out or mirrored, and its polar axis must be vertical. Try a case and an amplitude, and check the validity warning with `cap_shear` at 5 N or more.
5. **Timing** needs an exported build with VSync off (the editor is allowed but is labelled "editor"). In Godot: Project > Export, choose "Windows Desktop" or "Linux x86_64" (export templates for your Godot version must be installed), export a release build to `godot/build/`, then run the executable with `-- --benchmark` (on Windows use the `.console.exe` to see the output). It writes `results/godot/timing/<level>_<case>.json` for each timing case.

## Working rules

- Write only inside what you own (CONTRACT 2). To request a change in someone else's output, ask the user to relay it.
- `CONTRACT.md` changes only through the user.
- A change to `config/test_settings.json` after M1 invalidates other roles' results and must be announced.
- `solver/export/<level>/G.bin` is not in git. Generate it with `python solver/export.py` before running the Godot scene.
- Result files other than `G.bin` are committed, so that `compare/compare.py` runs from a clean checkout.
