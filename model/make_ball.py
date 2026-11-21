#!/usr/bin/env python3
"""
Role 1a: Sphere tetrahedral mesh and surface generator (model/make_ball.py).

Conforms to CONTRACT.md (sections 3, 6.1, 6.3) and model/README.md:
- Reads geometry and mesh parameters from config/test_settings.json (never hard-coded).
- Generates roughly uniform first-order (P1) tetrahedral sphere meshes via the Gmsh
  Python API (OpenCASCADE kernel).
- Writes model/<level>/ball.msh (Gmsh MSH 2.2 ASCII, contiguous 1..N tags, no unused
  nodes) and model/<level>/surface.json (with surface_areas and surface_triangles
  indexed by surface rows).
- Provides --check to assert all acceptance criteria and print a pass/fail table.

Usage:
    python model/make_ball.py
    python model/make_ball.py --levels coarse
    python model/make_ball.py --check
    python model/make_ball.py --config tmp_test_settings.json --levels coarse
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from collections import defaultdict
from pathlib import Path
from typing import Any

import numpy as np


# ---------------------------------------------------------------------------
# Tetrahedral geometry utilities
# ---------------------------------------------------------------------------

def tet_signed_volumes(nodes: np.ndarray, tets: np.ndarray) -> np.ndarray:
    """Return signed volumes of P1 tetrahedra, shape (n_tets,)."""
    p0 = nodes[tets[:, 0]]
    p1 = nodes[tets[:, 1]]
    p2 = nodes[tets[:, 2]]
    p3 = nodes[tets[:, 3]]
    return np.einsum("ij,ij->i", np.cross(p1 - p0, p2 - p0), p3 - p0) / 6.0


def orient_tets_positive(nodes: np.ndarray, tets: np.ndarray) -> np.ndarray:
    """Ensure every tetrahedron has strictly positive signed volume."""
    tets = np.array(tets, dtype=np.int64, copy=True)
    vols = tet_signed_volumes(nodes, tets)
    neg = vols < 0.0
    if np.any(neg):
        tets[neg, 1], tets[neg, 2] = tets[neg, 2].copy(), tets[neg, 1].copy()
    return tets


def extract_boundary_triangles_node_idx(nodes: np.ndarray, tets: np.ndarray) -> np.ndarray:
    """
    Extract boundary triangles belonging to exactly one tetrahedron,
    oriented counter-clockwise when viewed from outside (normal . centroid > 0).
    Returns array of shape (n_tris, 3) with 0-based global node indices.
    """
    face_counts: dict[tuple[int, int, int], int] = defaultdict(int)
    face_orig: dict[tuple[int, int, int], tuple[int, int, int]] = {}

    for a, b, c, d in tets:
        a, b, c, d = int(a), int(b), int(c), int(d)
        for f in ((b, c, d), (a, c, d), (a, b, d), (a, b, c)):
            key = tuple(sorted(f))
            face_counts[key] += 1
            face_orig[key] = f

    boundary = [face_orig[k] for k, cnt in face_counts.items() if cnt == 1]
    if not boundary:
        raise RuntimeError("No boundary faces found; mesh is empty or closed improperly.")

    tris = np.array(boundary, dtype=np.int64)
    p0 = nodes[tris[:, 0]]
    p1 = nodes[tris[:, 1]]
    p2 = nodes[tris[:, 2]]
    normals = np.cross(p1 - p0, p2 - p0)
    centroids = (p0 + p1 + p2) / 3.0
    inward = np.einsum("ij,ij->i", normals, centroids) < 0.0
    if np.any(inward):
        tris[inward, 1], tris[inward, 2] = (
            tris[inward, 2].copy(),
            tris[inward, 1].copy(),
        )

    order = np.lexsort((tris[:, 2], tris[:, 1], tris[:, 0]))
    return tris[order]


# ---------------------------------------------------------------------------
# MSH 2.2 ASCII I/O
# ---------------------------------------------------------------------------

def write_msh22_ascii(path: Path, nodes: np.ndarray, tets: np.ndarray) -> None:
    """
    Write Gmsh MSH 2.2 ASCII with contiguous 1..N node tags, zero unused nodes,
    and first-order tetrahedra (element type 4).
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    n_nodes = nodes.shape[0]
    n_tets = tets.shape[0]

    with path.open("w", encoding="ascii", newline="\n") as fh:
        fh.write("$MeshFormat\n")
        fh.write("2.2 0 8\n")
        fh.write("$EndMeshFormat\n")
        fh.write("$Nodes\n")
        fh.write(f"{n_nodes}\n")
        for idx in range(n_nodes):
            x, y, z = nodes[idx]
            fh.write(f"{idx + 1} {x:.16g} {y:.16g} {z:.16g}\n")
        fh.write("$EndNodes\n")
        fh.write("$Elements\n")
        fh.write(f"{n_tets}\n")
        for elem_idx in range(n_tets):
            n0, n1, n2, n3 = tets[elem_idx]
            # elem_id, elem_type=4 (4-node tet), num_tags=2, phys_tag=1, geom_tag=1
            fh.write(f"{elem_idx + 1} 4 2 1 1 {n0 + 1} {n1 + 1} {n2 + 1} {n3 + 1}\n")
        fh.write("$EndElements\n")


def read_msh22_ascii(path: Path) -> tuple[np.ndarray, np.ndarray, dict[str, Any]]:
    """
    Parse a Gmsh MSH 2.2 ASCII file and return (nodes, tets, meta).
    nodes: float64 (N, 3)
    tets:  int64 (M, 4), 0-based indices
    meta:  format diagnostics for --check
    """
    lines = path.read_text(encoding="ascii").splitlines()
    i = 0
    n_lines = len(lines)

    fmt_line = ""
    node_tags: list[int] = []
    coords: list[list[float]] = []
    tets: list[list[int]] = []
    other_elem_types: set[int] = set()

    while i < n_lines:
        token = lines[i].strip()
        if token == "$MeshFormat":
            fmt_line = lines[i + 1].strip()
            i += 3
        elif token == "$Nodes":
            count = int(lines[i + 1].strip())
            for k in range(count):
                parts = lines[i + 2 + k].split()
                node_tags.append(int(parts[0]))
                coords.append([float(parts[1]), float(parts[2]), float(parts[3])])
            i += count + 3
        elif token == "$Elements":
            count = int(lines[i + 1].strip())
            for k in range(count):
                parts = lines[i + 2 + k].split()
                etype = int(parts[1])
                ntags = int(parts[2])
                conn = [int(v) - 1 for v in parts[3 + ntags :]]
                if etype == 4:
                    tets.append(conn)
                else:
                    other_elem_types.add(etype)
            i += count + 3
        else:
            i += 1

    nodes_arr = np.asarray(coords, dtype=np.float64)
    tets_arr = np.asarray(tets, dtype=np.int64)
    used_nodes = set(tets_arr.ravel().tolist()) if tets_arr.size else set()

    meta = {
        "format_line": fmt_line,
        "node_tags_contiguous": node_tags == list(range(1, len(node_tags) + 1)),
        "unused_node_count": len(node_tags) - len(used_nodes),
        "other_elem_types": sorted(other_elem_types),
    }
    return nodes_arr, tets_arr, meta


# ---------------------------------------------------------------------------
# Surface computation (computed purely from mesh, CONTRACT 6.3)
# ---------------------------------------------------------------------------

def compute_surface(
    nodes: np.ndarray,
    tets: np.ndarray,
    mesh_level: str,
    radius_m: float,
    fixed_cap_deg: float,
) -> dict[str, Any]:
    """
    Compute surface topology, lumped surface areas, and fixed cap directly from
    the tetrahedral mesh (independent of Gmsh entity numbering).

    CONTRACT 6.3:
    - surface_nodes: sorted ascending 0-based global node indices
    - surface_positions: rest coordinates aligned with surface_nodes
    - surface_areas: 1/3 of the summed area of triangles containing each surface row
    - surface_triangles: outward CCW triangles using SURFACE ROW indices (0..n_surface-1)
    - fixed_nodes: 0-based global node indices on the surface with z/|x| <= -cos(fixed_cap_deg)
    """
    tris_node_idx = extract_boundary_triangles_node_idx(nodes, tets)
    surface_nodes = np.unique(tris_node_idx.ravel())
    surface_nodes.sort()
    n_surface = int(surface_nodes.shape[0])

    # Map global node index -> surface row (0..n_surface-1)
    node_to_row = {int(nid): row for row, nid in enumerate(surface_nodes)}
    surface_triangles = np.vectorize(node_to_row.__getitem__)(tris_node_idx).astype(np.int64)
    # Deterministic sort by surface row indices
    order = np.lexsort((surface_triangles[:, 2], surface_triangles[:, 1], surface_triangles[:, 0]))
    surface_triangles = surface_triangles[order]

    surface_positions = nodes[surface_nodes]
    surf_norms = np.linalg.norm(surface_positions, axis=1)
    cos_theta = surface_positions[:, 2] / surf_norms

    # Fixed cap at -z pole: z / |x| <= -cos(fixed_cap_deg)
    fixed_thresh = -math.cos(math.radians(fixed_cap_deg))
    fixed_mask = cos_theta <= fixed_thresh + 1e-15
    fixed_nodes = surface_nodes[fixed_mask].tolist()

    # Lumped nodal surface areas: 1/3 of each incident surface triangle's area
    p0 = surface_positions[surface_triangles[:, 0]]
    p1 = surface_positions[surface_triangles[:, 1]]
    p2 = surface_positions[surface_triangles[:, 2]]
    tri_areas = 0.5 * np.linalg.norm(np.cross(p1 - p0, p2 - p0), axis=1)

    surface_areas = np.zeros(n_surface, dtype=np.float64)
    share = tri_areas / 3.0
    np.add.at(surface_areas, surface_triangles[:, 0], share)
    np.add.at(surface_areas, surface_triangles[:, 1], share)
    np.add.at(surface_areas, surface_triangles[:, 2], share)

    return {
        "mesh_level": mesh_level,
        "radius_m": float(radius_m),
        "n_nodes": int(nodes.shape[0]),
        "n_tets": int(tets.shape[0]),
        "n_surface": n_surface,
        "fixed_nodes": fixed_nodes,
        "surface_nodes": surface_nodes.tolist(),
        "surface_positions": surface_positions.tolist(),
        "surface_areas": surface_areas.tolist(),
        "surface_triangles": surface_triangles.tolist(),
    }


# ---------------------------------------------------------------------------
# Gmsh sphere generation (roughly uniform P1 tetrahedra)
# ---------------------------------------------------------------------------

def _mesh_sphere_once(radius_m: float, h_global: float) -> tuple[np.ndarray, np.ndarray]:
    """Run Gmsh OpenCASCADE uniform sphere meshing once and return pruned (nodes, tets)."""
    import gmsh  # Local import so --check works even without Gmsh installed

    gmsh.initialize()
    try:
        gmsh.option.setNumber("General.Terminal", 0)
        gmsh.model.add("rehab_ball")

        sphere_tag = gmsh.model.occ.addSphere(0.0, 0.0, 0.0, radius_m)
        gmsh.model.occ.synchronize()
        gmsh.model.addPhysicalGroup(3, [sphere_tag], tag=1)

        gmsh.option.setNumber("Mesh.ElementOrder", 1)
        gmsh.option.setNumber("Mesh.MshFileVersion", 2.2)
        gmsh.option.setNumber("Mesh.Binary", 0)
        gmsh.option.setNumber("Mesh.Algorithm3D", 1)  # Delaunay
        gmsh.option.setNumber("Mesh.CharacteristicLengthMin", h_global * 0.9)
        gmsh.option.setNumber("Mesh.CharacteristicLengthMax", h_global * 1.1)

        gmsh.model.mesh.generate(3)
        gmsh.model.mesh.optimize("Netgen")

        node_tags, node_coords, _ = gmsh.model.mesh.getNodes()
        _, tet_node_tags = gmsh.model.mesh.getElementsByType(4)
        if tet_node_tags.size == 0:
            raise RuntimeError("Gmsh generated zero linear tetrahedra.")

        tag_to_coord = {
            int(tag): node_coords[3 * i : 3 * i + 3]
            for i, tag in enumerate(node_tags)
        }

        tet_tags_2d = tet_node_tags.reshape(-1, 4)
        used_tags = np.unique(tet_tags_2d)
        used_tags.sort()

        # Contiguous 0-based indexing with zero unused nodes
        tag_to_idx = {int(t): idx for idx, t in enumerate(used_tags)}
        nodes = np.array([tag_to_coord[int(t)] for t in used_tags], dtype=np.float64)
        tets = np.vectorize(tag_to_idx.__getitem__)(tet_tags_2d).astype(np.int64)
        tets = orient_tets_positive(nodes, tets)
        return nodes, tets
    finally:
        gmsh.finalize()


def generate_level(
    level: str,
    target_nodes: int,
    radius_m: float,
    fixed_cap_deg: float,
    out_dir: Path,
) -> tuple[np.ndarray, np.ndarray, dict[str, Any]]:
    """
    Tune uniform Gmsh element size so node count is within +-30% of target_nodes
    and fixed_nodes has >= 5 entries.
    """
    sphere_vol = (4.0 / 3.0) * math.pi * (radius_m ** 3)
    h = (sphere_vol / (0.65 * target_nodes)) ** (1.0 / 3.0)

    best_candidate: tuple[np.ndarray, np.ndarray, dict[str, Any]] | None = None

    for _ in range(10):
        nodes, tets = _mesh_sphere_once(radius_m, h)
        surface = compute_surface(nodes, tets, level, radius_m, fixed_cap_deg)
        n_nodes = nodes.shape[0]
        n_fixed = len(surface["fixed_nodes"])
        in_budget = abs(n_nodes - target_nodes) <= 0.30 * target_nodes

        best_candidate = (nodes, tets, surface)

        if in_budget and n_fixed >= 5:
            break

        ratio = (n_nodes / target_nodes) ** (1.0 / 3.0)
        h *= float(np.clip(ratio, 0.70, 1.45))

    assert best_candidate is not None
    nodes, tets, surface = best_candidate

    level_dir = out_dir / level
    write_msh22_ascii(level_dir / "ball.msh", nodes, tets)
    with (level_dir / "surface.json").open("w", encoding="utf-8") as fh:
        json.dump(surface, fh, indent=2)

    return nodes, tets, surface


# ---------------------------------------------------------------------------
# Acceptance checks (--check) per model/README.md & CONTRACT 6.3
# ---------------------------------------------------------------------------

def check_level(
    level: str,
    settings: dict[str, Any],
    model_dir: Path,
) -> list[tuple[str, bool, str]]:
    """Run all Role 1a acceptance checks for one mesh level."""
    results: list[tuple[str, bool, str]] = []
    geom = settings["geometry"]
    radius_m = float(geom["radius_m"])
    target_nodes = int(settings["mesh_levels"][level])

    msh_path = model_dir / level / "ball.msh"
    surf_path = model_dir / level / "surface.json"

    if not msh_path.exists() or not surf_path.exists():
        return [("Files exist", False, f"Missing {msh_path} or {surf_path}")]

    nodes, tets, meta = read_msh22_ascii(msh_path)
    surface = json.loads(surf_path.read_text(encoding="utf-8"))

    # 1. MSH 2.2 ASCII format & contiguous tags
    msh_ok = (
        meta["format_line"].startswith("2.2 0")
        and meta["node_tags_contiguous"]
        and meta["unused_node_count"] == 0
        and tets.shape[0] > 0
    )
    results.append((
        "MSH 2.2 ASCII & tags",
        msh_ok,
        f"fmt='{meta['format_line']}', contiguous={meta['node_tags_contiguous']}, "
        f"unused={meta['unused_node_count']}",
    ))

    # 2. Positive tet volumes & sphere volume tolerance
    vols = tet_signed_volumes(nodes, tets)
    min_vol = float(np.min(vols))
    total_vol = float(np.sum(vols))
    exact_vol = (4.0 / 3.0) * math.pi * (radius_m ** 3)
    vol_err = abs(total_vol - exact_vol) / exact_vol
    vol_tol = 0.05 if level == "coarse" else 0.02
    results.append((
        "Positive tet volumes",
        min_vol > 0.0,
        f"min_vol={min_vol:.3e} m^3",
    ))
    results.append((
        f"Total volume (<= {vol_tol * 100:.0f}%)",
        vol_err <= vol_tol,
        f"err={vol_err * 100:.2f}% (mesh={total_vol:.6e}, exact={exact_vol:.6e})",
    ))

    # 3. Node count within +-30%
    n_nodes = int(nodes.shape[0])
    rel_nodes = abs(n_nodes - target_nodes) / target_nodes
    results.append((
        "Node count (+-30%)",
        rel_nodes <= 0.30,
        f"n={n_nodes} vs target={target_nodes} ({(n_nodes - target_nodes) / target_nodes * 100:+.1f}%)",
    ))

    # 4. surface.json schema & coordinate match (CONTRACT 6.3)
    required_keys = {
        "mesh_level",
        "radius_m",
        "n_nodes",
        "n_tets",
        "n_surface",
        "fixed_nodes",
        "surface_nodes",
        "surface_positions",
        "surface_areas",
        "surface_triangles",
    }
    has_keys = required_keys.issubset(surface.keys())
    surf_nodes = surface.get("surface_nodes", [])
    n_surface = len(surf_nodes)
    surf_pos = np.asarray(surface.get("surface_positions", []), dtype=np.float64)
    surf_areas = np.asarray(surface.get("surface_areas", []), dtype=np.float64)
    tris = np.asarray(surface.get("surface_triangles", []), dtype=np.int64)

    schema_ok = (
        has_keys
        and surface["mesh_level"] == level
        and surface["n_nodes"] == n_nodes
        and surface["n_tets"] == int(tets.shape[0])
        and surface["n_surface"] == n_surface
        and surf_nodes == sorted(surf_nodes)
        and len(set(surf_nodes)) == n_surface
        and surf_pos.shape == (n_surface, 3)
        and surf_areas.shape == (n_surface,)
        and tris.ndim == 2
        and tris.shape[1] == 3
        and np.allclose(surf_pos, nodes[surf_nodes], atol=1e-12, rtol=1e-12)
    )
    results.append((
        "surface.json schema",
        schema_ok,
        f"n_surface={n_surface}, n_tris={tris.shape[0] if tris.ndim == 2 else 0}",
    ))

    # 5. fixed_nodes >= 5 and subset of surface_nodes
    surf_set = set(surf_nodes)
    fixed_nodes = surface.get("fixed_nodes", [])
    fixed_set = set(fixed_nodes)
    fixed_ok = len(fixed_nodes) >= 5 and fixed_set.issubset(surf_set)
    results.append((
        "fixed_nodes (>=5, surf)",
        fixed_ok,
        f"n_fixed={len(fixed_nodes)}, subset={fixed_set.issubset(surf_set)}",
    ))

    # 6. surface_triangles valid surface rows, all rows covered, outward-oriented
    rows_valid = (
        tris.size > 0
        and int(np.min(tris)) >= 0
        and int(np.max(tris)) < n_surface
    )
    all_rows_used = rows_valid and (len(np.unique(tris.ravel())) == n_surface)
    if rows_valid:
        p0, p1, p2 = surf_pos[tris[:, 0]], surf_pos[tris[:, 1]], surf_pos[tris[:, 2]]
        tri_normals = np.cross(p1 - p0, p2 - p0)
        tri_centroids = (p0 + p1 + p2) / 3.0
        dots = np.einsum("ij,ij->i", tri_normals, tri_centroids)
        outward_ok = bool(np.all(dots > 0.0))
        min_dot = float(np.min(dots))
        tri_total_area = float(np.sum(0.5 * np.linalg.norm(tri_normals, axis=1)))
    else:
        outward_ok = False
        min_dot = float("nan")
        tri_total_area = 0.0

    results.append((
        "Surface rows & coverage",
        rows_valid and all_rows_used,
        f"valid_range={rows_valid}, all_covered={all_rows_used}",
    ))
    results.append((
        "Outward surface tris",
        outward_ok,
        f"min(n . c)={min_dot:.3e}",
    ))

    # 7. surface_areas positive and sum to total triangle area (rel 1e-12)
    areas_pos = surf_areas.size == n_surface and bool(np.all(surf_areas > 0.0))
    area_sum = float(np.sum(surf_areas))
    rel_area_err = (
        abs(area_sum - tri_total_area) / tri_total_area
        if tri_total_area > 0.0
        else float("inf")
    )
    results.append((
        "surface_areas >0 & sum",
        areas_pos and rel_area_err <= 1e-12,
        f"min={float(np.min(surf_areas)):.3e}, rel_err={rel_area_err:.2e}",
    ))

    # 8. Optional scikit-fem integration check (O2)
    try:
        from skfem import MeshTet

        sk_mesh = MeshTet.load(str(msh_path))
        sk_nodes = sk_mesh.p.T
        sk_tets = sk_mesh.t.T
        sk_vol = float(np.sum(np.abs(tet_signed_volumes(sk_nodes, sk_tets))))
        sk_ok = (
            sk_nodes.shape == nodes.shape
            and np.allclose(sk_nodes, nodes, atol=1e-12, rtol=1e-12)
            and abs(sk_vol - total_vol) / total_vol < 1e-12
        )
        results.append((
            "scikit-fem MeshTet.load",
            sk_ok,
            f"nodes={sk_nodes.shape[0]}, vol_diff={abs(sk_vol - total_vol):.2e}",
        ))
    except ImportError:
        results.append((
            "scikit-fem MeshTet.load",
            True,
            "SKIP (skfem not installed in this environment)",
        ))

    return results


def print_check_table(all_results: dict[str, list[tuple[str, bool, str]]]) -> bool:
    """Print a formatted pass/fail table and return True iff all checks pass."""
    all_passed = True
    header = f"{'Level':<8} | {'Check':<24} | {'Status':<6} | Details"
    print(header)
    print("-" * (len(header) + 34))
    for level, checks in all_results.items():
        for name, ok, detail in checks:
            status = "PASS" if ok else "FAIL"
            if not ok:
                all_passed = False
            print(f"{level:<8} | {name:<24} | {status:<6} | {detail}")
    print("-" * (len(header) + 34))
    print(f"OVERALL: {'ALL CHECKS PASSED' if all_passed else 'FAILED'}")
    return all_passed


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main() -> int:
    default_root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(
        description="Role 1a: Generate and validate tetrahedral ball meshes and surface.json."
    )
    parser.add_argument(
        "--config",
        type=Path,
        default=default_root / "config" / "test_settings.json",
        help="Path to config/test_settings.json",
    )
    parser.add_argument(
        "--out-dir",
        type=Path,
        default=default_root / "model",
        help="Output directory for <level>/ball.msh and surface.json",
    )
    parser.add_argument(
        "--levels",
        nargs="+",
        default=None,
        help="Optional subset of levels (e.g., coarse medium fine)",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Validate existing model/<level>/ball.msh and surface.json without regenerating",
    )
    args = parser.parse_args()

    if not args.config.exists():
        print(f"ERROR: Settings file not found: {args.config}", file=sys.stderr)
        return 1

    settings = json.loads(args.config.read_text(encoding="utf-8"))
    mesh_levels: dict[str, int] = settings["mesh_levels"]
    levels = args.levels if args.levels is not None else list(mesh_levels.keys())

    for lvl in levels:
        if lvl not in mesh_levels:
            print(
                f"ERROR: Unknown mesh level '{lvl}' (expected {list(mesh_levels)})",
                file=sys.stderr,
            )
            return 1

    if not args.check:
        geom = settings["geometry"]
        radius_m = float(geom["radius_m"])
        fixed_cap_deg = float(geom["fixed_cap_deg"])

        for lvl in levels:
            target = int(mesh_levels[lvl])
            nodes, tets, surface = generate_level(
                lvl, target, radius_m, fixed_cap_deg, args.out_dir
            )
            print(
                f"Generated {lvl}: {nodes.shape[0]} nodes (target {target}), "
                f"{tets.shape[0]} tets, {surface['n_surface']} surface nodes, "
                f"fixed={len(surface['fixed_nodes'])}"
            )

    all_results = {lvl: check_level(lvl, settings, args.out_dir) for lvl in levels}
    ok = print_check_table(all_results)
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())