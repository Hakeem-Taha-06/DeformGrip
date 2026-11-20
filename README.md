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

## Working rules

- Write only inside what you own (CONTRACT 2). To request a change in someone else's output, ask the user to relay it.
- `CONTRACT.md` changes only through the user.
- A change to `config/test_settings.json` after M1 invalidates other roles' results and must be announced.
- `solver/export/<level>/G.bin` is not in git. Generate it with `python solver/export.py` before running the Godot scene.
- Result files other than `G.bin` are committed, so that `compare/compare.py` runs from a clean checkout.
