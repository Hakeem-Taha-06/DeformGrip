# Role 1b: Godot Scene

> One person. Read `../CONTRACT.md` first; it is authoritative. If this file contradicts it, tell the user.

## Purpose
You build the interactive scene: it shows the ball, applies a load set that follows a chosen case and amplitude, and plays either our simulation or the SOFA one, side by side. Your input interface must stay open so that a VR controller supplied externally can plug in later (it is not part of this phase).

## Inputs
`model/<level>/surface.json`; `config/cases/<level>/*.json` and `config/profiles/*.csv`; `solver/export/<level>/G.bin` + `G.meta.json` (from 2b, regenerated locally, not in git); `results/sofa/playback/*` (from 3); the C# core in `core/` (from 2b).

## Deliverables
A Godot 4 **.NET (C#)** project in `godot/`, a main scene, `godot/VERSION.txt` (exact Godot version), timing files `results/godot/timing/*.json`, and dump files `results/godot/dumps/*.bin`.

## Tasks (in order)
1. Create the project directly in `godot/`. Use the .NET edition and C# only. Pin the exact Godot version in `godot/VERSION.txt`. Reference the core library from `godot/*.csproj` (`ProjectReference` or an include of `../core/`); do not copy core files into the project. Read data files by file-system path (default `../solver/export/` and `../config/`, configurable by an exported property), not through `res://`, so that large files are not duplicated.
2. **Mesh builder:** read `surface.json`, build an `ArrayMesh` from `surface_positions` and `surface_triangles` (already surface rows). Apply the coordinate map of CONTRACT 3 (Godot = (x, z, -y)) and the winding rule there. Recompute normals after each deformation. Choose the cheapest working vertex-update method and say which one in a code comment.
3. **Force input:** define `IForceInput` with a method that fills a buffer with the current load set (`NodalLoad` from the core) for a given time. Implement `CaseForceSource` (loads of one case file scaled by an amplitude from a slider, 0-20 N, or by a profile CSV replay). Do not write controller or sensor code; keep the interface open.
4. **"Ours" mode:** load `G.bin` through the core (`InfluenceMatrix`). Each rendered frame, build the load set and call the core, then write the vertex positions once: `rest + gain * u`. If `stage2.enabled`, advance the core's viscoelastic state in steps of `dt_force` through all force samples since the last frame.
5. **"SOFA" mode:** read the force-table playback file of the selected case from `results/sofa/playback/`; pick the frames around the current amplitude and interpolate linearly. Random cases have no playback; the toggle is disabled for them.
6. **Side-by-side toggle:** two balls (left = ours, right = SOFA) driven by the same case and amplitude at the same time. Show on screen: case id, amplitude, time, largest displacement, and the visual gain (default x1, adjustable up to x50, always labelled). When the largest displacement exceeds `validity.max_disp_fraction_of_radius` times the radius (from the settings), show a visible warning that the linear model is outside its validity range.
7. **Dump command:** a command-line flag (for example `--dump <level> <case> <profile>`) that replays a profile in "ours" mode and writes `results/godot/dumps/ours_<level>_<case>_<profile>.bin` (CONTRACT 6.7), so 2b's parity script can check it.
8. **Timing:** in an **exported build** (not the editor) with vsync off, replay `slow_squeeze` for each level and each case in `timing_cases` and write `results/godot/timing/<level>_<case>.json`. Record two measurements (CONTRACT 6.6): the core update only, and the whole frame, each with mean/p95/max and its own `what_is_timed`, plus the hardware and Godot version.

## Acceptance checks
- At rest, the ball is not inside-out, not mirrored, round, and oriented with the polar axis vertical (verify the coordinate map and winding by looking at it; open item O5).
- With `cap_shear` at 5 N or more the validity warning is visible; at 1 N it is not.
- With a constant 10 N amplitude on `cap_medium` at the `medium` level, the largest displacement shown equals Python's value within 1e-4 relative (checked through 2b's parity script).
- Switching between ours and SOFA does not change the camera, the case or the amplitude.
- The scene runs without SOFA or Python installed, given the data files.
- Timing files exist for all three levels and both timing cases; whole-frame time is reported against `targets.frame_ms_max`.

## Not your job
The math in the core classes (2b), generating the SOFA playback files (3), choosing test values and cases (4), any controller, sensor, VR or Arduino code.
