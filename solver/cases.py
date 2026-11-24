"""Settings, surface data and case loading for the solver (Role 2a).

Everything is read from the files in config/ and model/ (CONTRACT 6.1, 6.3, 6.4).
Nothing about the ball or the tests is hard-coded here, except the fallback for the
validity limit when the settings file has no `validity` key.
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[1]
SETTINGS_PATH = REPO / "config" / "test_settings.json"
DEFAULT_VALIDITY_FRACTION = 0.10


def load_settings(path: Path = SETTINGS_PATH) -> dict:
    return json.loads(Path(path).read_text(encoding="utf-8"))


def level_names(settings: dict) -> list[str]:
    return list(settings["mesh_levels"])


def case_ids(settings: dict, physical_only: bool = False) -> list[str]:
    ids = list(settings["cases"]["list"])
    if physical_only:
        ids = [c for c in ids if settings["cases"]["list"][c]["type"] != "random"]
    return ids


def validity_fraction(settings: dict) -> tuple[float, bool]:
    """Return (fraction of the radius, True if it came from the settings file)."""
    v = settings.get("validity", {}).get("max_disp_fraction_of_radius")
    if v is None:
        return DEFAULT_VALIDITY_FRACTION, False
    return float(v), True


def amplitude_tag(force) -> str:
    """Amplitude as written in the settings file followed by N (10 -> '10N', 2.5 -> '2.5N')."""
    return f"{force}N"


def load_surface(level: str, repo: Path = REPO) -> dict:
    s = json.loads((repo / "model" / level / "surface.json").read_text(encoding="utf-8"))
    out = {
        "mesh_level": s["mesh_level"],
        "radius_m": float(s["radius_m"]),
        "n_nodes": int(s["n_nodes"]),
        "n_tets": int(s["n_tets"]),
        "n_surface": int(s["n_surface"]),
        "fixed_nodes": np.asarray(s["fixed_nodes"], dtype=np.int64),
        "nodes": np.asarray(s["surface_nodes"], dtype=np.int64),
        "positions": np.asarray(s["surface_positions"], dtype=np.float64),
        "areas": np.asarray(s["surface_areas"], dtype=np.float64),
        "triangles": np.asarray(s["surface_triangles"], dtype=np.int64),
    }
    if out["mesh_level"] != level:
        raise ValueError(f"{level}: surface.json says mesh_level={out['mesh_level']}")
    if len(out["nodes"]) != out["n_surface"] or out["positions"].shape != (out["n_surface"], 3):
        raise ValueError(f"{level}: surface.json sizes are inconsistent")
    if not np.all(np.diff(out["nodes"]) > 0):
        raise ValueError(f"{level}: surface_nodes must be strictly ascending")
    if not np.isin(out["fixed_nodes"], out["nodes"]).all():
        raise ValueError(f"{level}: fixed_nodes must all be surface nodes")
    return out


@dataclass
class Case:
    case_id: str
    level: str
    n_surface: int
    physical: bool
    spec: dict
    rows: np.ndarray      # (n,) surface rows, ascending, unique
    f: np.ndarray         # (n, 3) nodal forces of the unit pattern [N]

    def pattern(self) -> np.ndarray:
        """Dense surface-DOF load vector of length 3*n_surface (DOF = 3*row + c)."""
        p = np.zeros((self.n_surface, 3))
        p[self.rows] = self.f
        return p.reshape(-1)


def load_case(level: str, case_id: str, n_surface: int | None = None, repo: Path = REPO) -> Case:
    d = json.loads((repo / "config" / "cases" / level / f"{case_id}.json").read_text(encoding="utf-8"))
    rows = np.asarray([l["row"] for l in d["loads"]], dtype=np.int64)
    f = np.asarray([l["f"] for l in d["loads"]], dtype=np.float64).reshape(-1, 3)
    if d["case_id"] != case_id or d["mesh_level"] != level:
        raise ValueError(f"{level}/{case_id}: case file names do not match its content")
    if n_surface is not None and d["n_surface"] != n_surface:
        raise ValueError(f"{level}/{case_id}: n_surface {d['n_surface']} != {n_surface}")
    if len(rows) == 0 or not np.all(np.diff(rows) > 0):
        raise ValueError(f"{level}/{case_id}: rows must be non-empty, unique and ascending")
    if rows[0] < 0 or rows[-1] >= d["n_surface"]:
        raise ValueError(f"{level}/{case_id}: a row is outside 0..n_surface-1")
    if not np.isfinite(f).all():
        raise ValueError(f"{level}/{case_id}: non-finite force")
    total = float(np.linalg.norm(f, axis=1).sum())
    if abs(total - 1.0) > 1e-12:
        raise ValueError(f"{level}/{case_id}: sum of force norms is {total!r}, expected 1")
    return Case(case_id, level, d["n_surface"], bool(d["physical"]), d["spec"], rows, f)
