# Role 1a: Model

> One person. Read `../CONTRACT.md` first; it is authoritative. If this file contradicts it, tell the user.

## Purpose
You build the 3D ball (broken into small tetrahedra) and describe its surface. Every other role loads this same ball, so the files must be exact.

## Inputs
`config/test_settings.json` (radius, fixed cap angle, mesh node targets). Never hard-code these.

## Deliverables
`model/<level>/ball.msh` and `model/<level>/surface.json` for `coarse`, `medium`, `fine` (formats: CONTRACT 6.3), plus `model/make_ball.py` that regenerates everything from the settings file.

## Tasks (in order)
1. `pip install gmsh`. Write `model/make_ball.py` that reads the settings and builds, for each level, a sphere of radius `radius_m` centred at the origin using the Gmsh Python API (OpenCASCADE kernel).
2. Mesh with first-order tetrahedra only and a roughly uniform element size. Tune the size so the node count is within +-30% of `mesh_levels[level]`.
3. Write MSH version **2.2 ASCII** (`Mesh.MshFileVersion = 2.2`). Node tags must be contiguous 1..N and in the same order as the coordinates; no unused nodes.
4. Compute `surface.json` from the mesh itself (not from Gmsh entity numbering):
   - boundary faces: the triangles that belong to exactly one tetrahedron; orient them outward;
   - `surface_nodes` (sorted), `surface_positions`, `surface_triangles` (as surface rows);
   - `surface_areas`: one third of the summed area of the triangles containing each surface node;
   - `fixed_nodes`: surface nodes with `z/|x| <= -cos(fixed_cap_deg)`.
5. Add `python model/make_ball.py --check` that asserts every acceptance check below and prints a pass/fail table.
6. Integration test with 2a and 3 (open item O2): confirm the same `ball.msh` loads in scikit-fem (`MeshTet.load`) and in SOFA, with equal node counts, equal total tet volume, and `mesh.p.T` equal to the coordinates in the file.

## Acceptance checks (all must pass)
- All tetrahedra have positive volume; the sum of volumes is within 5% (coarse) or 2% (medium, fine) of `4/3*pi*R^3` (the mesh is a polyhedral approximation of the sphere, so a small deficit is normal).
- Node count within +-30% of the target for each level.
- `fixed_nodes` has at least 5 entries and all of them are in `surface_nodes`.
- `surface_triangles` are outward-oriented (normal dot centroid > 0 for all); every surface node appears in at least one triangle and every triangle vertex is a valid row.
- `surface_areas` are positive and sum to the total area of the surface triangles (relative 1e-12).
- `surface.json` validates against CONTRACT 6.3; `ball.msh` loads in scikit-fem and SOFA.

## Not your job
Solving, SOFA scenes, the Godot scene, choosing the numbers in the settings file or the cases (Role 4).
