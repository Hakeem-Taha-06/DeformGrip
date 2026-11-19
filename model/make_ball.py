#!/usr/bin/env python3
"""
Role 1a: Sphere tetrahedral mesh and region generator (model/make_ball.py).

Reads geometry and mesh parameters from config/test_settings.json (never hard-coded),
generates first-order tetrahedral sphere meshes via the Gmsh Python API (OpenCASCADE
kernel), writes MSH 2.2 ASCII files and regions.json for each mesh level, and provides
the --check acceptance validation suite.

Usage:
    python model/make_ball.py
    python model/make_ball.py --levels coarse
    python model/make_ball.py --check
    python model/make_ball.py --config config/test_settings.json --check
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


def extract_boundary_triangles(nodes: np.ndarray, tets: np.ndarray) -> np.ndarray:
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

    # Deterministic ordering
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
# Region & patch computation (computed purely from mesh)
# ---------------------------------------------------------------------------

def compute_regions(
    nodes: np.ndarray,
    tets: np.ndarray,
    mesh_level: str,
    radius_m: float,
    fixed_cap_deg: float,
    patches_deg: dict[str, float],
) -> dict[str, Any]:
    """
    Compute surface topology, fixed cap, and nested load patches directly from
    the tetrahedral mesh (independent of Gmsh entity numbering).
    """
    surface_triangles = extract_boundary_triangles(nodes, tets)
    surface_nodes = np.unique(surface_triangles.ravel())
    surface_nodes.sort()

    surface_positions = nodes[surface_nodes]
    surf_norms = np.linalg.norm(surface_positions, axis=1)
    cos_theta = surface_positions[:, 2] / surf_norms

    # Fixed cap at -z pole: z / |x| <= -cos(fixed_cap_deg)
    fixed_thresh = -math.cos(math.radians(fixed_cap_deg))
    fixed_mask = cos_theta <= fixed_thresh + 1e-15
    fixed_nodes = surface_nodes[fixed_mask].tolist()

    # Precompute surface triangle areas once
    p0 = nodes[surface_triangles[:, 0]]
    p1 = nodes[surface_triangles[:, 1]]
    p2 = nodes[surface_triangles[:, 2]]
    tri_areas = 0.5 * np.linalg.norm(np.cross(p1 - p0, p2 - p0), axis=1)

    patches_out: dict[str, Any] = {}
    for patch_name, half_angle_deg in patches_deg.items():
        patch_thresh = math.cos(math.radians(half_angle_deg))
        patch_mask = cos_theta >= patch_thresh - 1e-15
        patch_nodes_arr = surface_nodes[patch_mask]
        patch_nodes = patch_nodes_arr.tolist()
        patch_set = set(patch_nodes)

        # Area weights: 1/3 of every surface triangle with all 3 vertices in patch
        raw_weights = {nid: 0.0 for nid in patch_nodes}
        for tri_idx in range(surface_triangles.shape[0]):
            n0, n1, n2 = (
                int(surface_triangles[tri_idx, 0]),
                int(surface_triangles[tri_idx, 1]),
                int(surface_triangles[tri_idx, 2]),
            )
            if n0 in patch_set and n1 in patch_set and n2 in patch_set:
                share = float(tri_areas[tri_idx]) / 3.0
                raw_weights[n0] += share
                raw_weights[n1] += share
                raw_weights[n2] += share

        total_area = sum(raw_weights.values())
        if total_area > 0.0:
            weights = [raw_weights[nid] / total_area for nid in patch_nodes]
        else:
            weights = [0.0 for _ in patch_nodes]

        # Inward unit normals: -x / |x|
        p_coords = nodes[patch_nodes_arr]
        p_norms = np.linalg.norm(p_coords, axis=1, keepdims=True)
        inward_normals = (-p_coords / p_norms).tolist() if len(patch_nodes) else []

        patches_out[patch_name] = {
            "half_angle_deg": half_angle_deg,
            "nodes": patch_nodes,
            "weights": weights,
            "normals": inward_normals,
        }

    return {
        "mesh_level": mesh_level,
        "radius_m": float(radius_m),
        "n_nodes": int(nodes.shape[0]),
        "n_tets": int(tets.shape[0]),
        "fixed_nodes": fixed_nodes,
        "surface_nodes": surface_nodes.tolist(),
        "surface_positions": surface_positions.tolist(),
        "surface_triangles": surface_triangles.tolist(),
        "patches": patches_out,
    }


# ---------------------------------------------------------------------------
# Gmsh sphere generation with automatic node-count & pole-refinement tuning
# ---------------------------------------------------------------------------

def _mesh_sphere_once(
    radius_m: float,
    h_global: float,
    pole_refine_factor: float,
    pole_radius: float,
) -> tuple[np.ndarray, np.ndarray]:
    """Run Gmsh OpenCASCADE sphere meshing once and return pruned (nodes, tets)."""
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

        if pole_refine_factor < 0.999:
            # Local Ball refinement fields near +z (load) and -z (fixed) poles
            f_top = gmsh.model.mesh.field.add("Ball")
            gmsh.model.mesh.field.setNumber(f_top, "XCenter", 0.0)
            gmsh.model.mesh.field.setNumber(f_top, "YCenter", 0.0)
            gmsh.model.mesh.field.setNumber(f_top, "ZCenter", radius_m)
            gmsh.model.mesh.field.setNumber(f_top, "Radius", pole_radius)
            gmsh.model.mesh.field.setNumber(f_top, "Thickness", pole_radius)
            gmsh.model.mesh.field.setNumber(f_top, "VIn", h_global * pole_refine_factor)
            gmsh.model.mesh.field.setNumber(f_top, "VOut", h_global)

            f_bot = gmsh.model.mesh.field.add("Ball")
            gmsh.model.mesh.field.setNumber(f_bot, "XCenter", 0.0)
            gmsh.model.mesh.field.setNumber(f_bot, "YCenter", 0.0)
            gmsh.model.mesh.field.setNumber(f_bot, "ZCenter", -radius_m)
            gmsh.model.mesh.field.setNumber(f_bot, "Radius", pole_radius)
            gmsh.model.mesh.field.setNumber(f_bot, "Thickness", pole_radius)
            gmsh.model.mesh.field.setNumber(f_bot, "VIn", h_global * pole_refine_factor)
            gmsh.model.mesh.field.setNumber(f_bot, "VOut", h_global)

            f_min = gmsh.model.mesh.field.add("Min")
            gmsh.model.mesh.field.setNumbers(f_min, "FieldsList", [f_top, f_bot])
            gmsh.model.mesh.field.setAsBackgroundMesh(f_min)

            gmsh.option.setNumber("Mesh.MeshSizeExtendFromBoundary", 0)
            gmsh.option.setNumber("Mesh.MeshSizeFromPoints", 0)
            gmsh.option.setNumber("Mesh.MeshSizeFromCurvature", 0)
        else:
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
    patches_deg: dict[str, float],
    out_dir: Path,
) -> tuple[np.ndarray, np.ndarray, dict[str, Any]]:
    """
    Tune Gmsh element size (and pole refinement if needed) so node count is within
    +-30% of target_nodes and every patch has >= 5 nodes with strictly positive weights.
    """
    sphere_vol = (4.0 / 3.0) * math.pi * (radius_m ** 3)
    h = (sphere_vol / (0.65 * target_nodes)) ** (1.0 / 3.0)
    min_patch_deg = min(patches_deg.values())
    pole_radius = radius_m * math.sin(math.radians(min_patch_deg)) * 1.25

    pole_factor = 1.0
    best_candidate: tuple[np.ndarray, np.ndarray, dict[str, Any]] | None = None

    for _ in range(12):
        nodes, tets = _mesh_sphere_once(radius_m, h, pole_factor, pole_radius)
        regions = compute_regions(
            nodes, tets, level, radius_m, fixed_cap_deg, patches_deg
        )
        n_nodes = nodes.shape[0]
        min_patch_count = min(len(p["nodes"]) for p in regions["patches"].values())
        min_weight = min(
            (min(p["weights"]) if p["weights"] else 0.0)
            for p in regions["patches"].values()
        )
        in_budget = abs(n_nodes - target_nodes) <= 0.30 * target_nodes
        patches_ok = (min_patch_count >= 5) and (min_weight > 0.0)

        best_candidate = (nodes, tets, regions)

        if in_budget and patches_ok:
            break

        if not patches_ok:
            pole_factor *= 0.70
            # Slightly coarsen global h if pole refinement pushes node count up
            if n_nodes > 1.15 * target_nodes:
                h *= (n_nodes / target_nodes) ** (1.0 / 3.0)
        else:
            # Smoothly adjust h toward target_nodes
            ratio = (n_nodes / target_nodes) ** (1.0 / 3.0)
            h *= float(np.clip(ratio, 0.70, 1.45))

    assert best_candidate is not None
    nodes, tets, regions = best_candidate

    min_patch_count = min(len(p["nodes"]) for p in regions["patches"].values())
    if min_patch_count < 5:
        print(
            f"[WARNING] Level '{level}': smallest patch has {min_patch_count} nodes (< 5). "
            "Ask Role 4 to widen patches_deg in config/test_settings.json.",
            file=sys.stderr,
        )

    level_dir = out_dir / level
    write_msh22_ascii(level_dir / "ball.msh", nodes, tets)
    with (level_dir / "regions.json").open("w", encoding="utf-8") as fh:
        json.dump(regions, fh, indent=2)

    return nodes, tets, regions


# ---------------------------------------------------------------------------
# Acceptance checks (--check)
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
    patches_deg = geom["patches_deg"]
    target_nodes = int(settings["mesh_levels"][level])

    msh_path = model_dir / level / "ball.msh"
    reg_path = model_dir / level / "regions.json"

    if not msh_path.exists() or not reg_path.exists():
        return [("Files exist", False, f"Missing {msh_path} or {reg_path}")]

    nodes, tets, meta = read_msh22_ascii(msh_path)
    regions = json.loads(reg_path.read_text(encoding="utf-8"))

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

    # 4. Schema & coordinate match
    required_keys = {
        "mesh_level",
        "radius_m",
        "n_nodes",
        "n_tets",
        "fixed_nodes",
        "surface_nodes",
        "surface_positions",
        "surface_triangles",
        "patches",
    }
    has_keys = required_keys.issubset(regions.keys())
    surf_nodes = regions.get("surface_nodes", [])
    surf_pos = np.asarray(regions.get("surface_positions", []), dtype=np.float64)
    schema_ok = (
        has_keys
        and regions["mesh_level"] == level
        and regions["n_nodes"] == n_nodes
        and regions["n_tets"] == int(tets.shape[0])
        and surf_nodes == sorted(surf_nodes)
        and surf_pos.shape == (len(surf_nodes), 3)
        and np.allclose(surf_pos, nodes[surf_nodes], atol=1e-12, rtol=1e-12)
    )
    results.append((
        "regions.json schema",
        schema_ok,
        f"n_surface={len(surf_nodes)}, n_tris={len(regions.get('surface_triangles', []))}",
    ))

    # 5. Outward-oriented surface triangles
    tris = np.asarray(regions["surface_triangles"], dtype=np.int64)
    p0, p1, p2 = nodes[tris[:, 0]], nodes[tris[:, 1]], nodes[tris[:, 2]]
    tri_normals = np.cross(p1 - p0, p2 - p0)
    tri_centroids = (p0 + p1 + p2) / 3.0
    dots = np.einsum("ij,ij->i", tri_normals, tri_centroids)
    results.append((
        "Outward surface tris",
        bool(np.all(dots > 0.0)),
        f"min(n . c)={float(np.min(dots)):.3e}",
    ))

    # 6. Region topology: disjoint fixed/patches, nested patches, >= 5 nodes, subset of surface
    surf_set = set(surf_nodes)
    fixed_set = set(regions["fixed_nodes"])
    sorted_patches = sorted(patches_deg.items(), key=lambda kv: kv[1])

    disjoint_ok = len(fixed_set) > 0 and fixed_set.issubset(surf_set)
    nested_ok = True
    count_ok = True
    prev_set: set[int] | None = None
    counts_summary: list[str] = []

    for pname, _ in sorted_patches:
        pinfo = regions["patches"][pname]
        pset = set(pinfo["nodes"])
        counts_summary.append(f"{pname}={len(pset)}")
        if not pset.issubset(surf_set) or not fixed_set.isdisjoint(pset):
            disjoint_ok = False
        if prev_set is not None and not prev_set.issubset(pset):
            nested_ok = False
        if len(pset) < 5:
            count_ok = False
        prev_set = pset

    results.append((
        "Disjoint & on surface",
        disjoint_ok,
        f"fixed={len(fixed_set)}, {', '.join(counts_summary)}",
    ))
    results.append((
        "Nested patches & >=5",
        nested_ok and count_ok,
        f"nested={nested_ok}, counts=({', '.join(counts_summary)})",
    ))

    # 7. Weights positive & sum to 1 (+-1e-12); unit inward normals
    weights_ok = True
    normals_ok = True
    max_w_err = 0.0
    max_n_err = 0.0

    for pname, _ in sorted_patches:
        pinfo = regions["patches"][pname]
        pnodes = np.asarray(pinfo["nodes"], dtype=np.int64)
        w = np.asarray(pinfo["weights"], dtype=np.float64)
        nrm = np.asarray(pinfo["normals"], dtype=np.float64)
        if w.size == 0 or np.any(w <= 0.0):
            weights_ok = False
        w_err = abs(float(np.sum(w)) - 1.0)
        max_w_err = max(max_w_err, w_err)
        if w_err > 1e-12:
            weights_ok = False

        lens = np.linalg.norm(nrm, axis=1)
        expected = -nodes[pnodes] / np.linalg.norm(nodes[pnodes], axis=1, keepdims=True)
        n_err = max(float(np.max(np.abs(lens - 1.0))), float(np.max(np.abs(nrm - expected))))
        max_n_err = max(max_n_err, n_err)
        if n_err > 1e-12:
            normals_ok = False

    results.append((
        "Weights >0 & sum=1",
        weights_ok,
        f"max |sum-1|={max_w_err:.2e}",
    ))
    results.append((
        "Inward unit normals",
        normals_ok,
        f"max err={max_n_err:.2e}",
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
    print("-" * len(header) + "-" * 30)
    for level, checks in all_results.items():
        for name, ok, detail in checks:
            status = "PASS" if ok else "FAIL"
            if not ok:
                all_passed = False
            print(f"{level:<8} | {name:<24} | {status:<6} | {detail}")
    print("-" * (len(header) + 30))
    print(f"OVERALL: {'ALL CHECKS PASSED' if all_passed else 'FAILED'}")
    return all_passed


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main() -> int:
    default_root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(
        description="Role 1a: Generate and validate tetrahedral ball meshes and regions."
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
        help="Output directory for <level>/ball.msh and regions.json",
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
        help="Validate existing model/<level>/ball.msh and regions.json without regenerating",
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
            print(f"ERROR: Unknown mesh level '{lvl}' (expected {list(mesh_levels)})", file=sys.stderr)
            return 1

    if not args.check:
        geom = settings["geometry"]
        radius_m = float(geom["radius_m"])
        fixed_cap_deg = float(geom["fixed_cap_deg"])
        patches_deg = {k: float(v) for k, v in geom["patches_deg"].items()}

        for lvl in levels:
            target = int(mesh_levels[lvl])
            nodes, tets, regions = generate_level(
                lvl, target, radius_m, fixed_cap_deg, patches_deg, args.out_dir
            )
            p_counts = {k: len(v["nodes"]) for k, v in regions["patches"].items()}
            print(
                f"Generated {lvl}: {nodes.shape[0]} nodes (target {target}), "
                f"{tets.shape[0]} tets, {len(regions['surface_nodes'])} surface nodes, "
                f"patches={p_counts}"
            )

    all_results = {lvl: check_level(lvl, settings, args.out_dir) for lvl in levels}
    ok = print_check_table(all_results)
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())