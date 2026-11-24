"""Finite-element core of "ours" (Role 2a).

Static linear elasticity on the P1 tetrahedral mesh, with the fixed cap removed from the
system and the stiffness matrix factorized once per mesh level (CONTRACT 4.1 - 4.3).

Conventions (CONTRACT 3): SI units; surface row = position in `surface_nodes`; surface DOF
index = 3*row + c; full-mesh DOF = 3*node + c (node-major, interleaved x, y, z).

Public API
    Level(name)                   one mesh level, factorized
      .solve_loads(F_s)           direct solve, surface loads -> (n_surface, 3) displacement [m]
      .influence_blocks(block)    generator of columns of G in blocks (what Role 2b exports)
      .G_columns(cols)            selected columns of G
      .influence_times(p)         G @ p using only the loaded columns, in blocks
      .build_influence()          the full matrix G (guarded: large on the fine level)
      .selftest()                 independent checks of the assembled system
    selftest_cube()               analytic check of the material law (E, nu)
"""
from __future__ import annotations

import time
from pathlib import Path

import meshio
import numpy as np
from scipy.sparse.linalg import splu
from skfem import Basis, ElementTetP1, ElementVector, MeshTet, asm
from skfem.models.elasticity import lame_parameters, linear_elasticity

from cases import REPO, load_settings, load_surface

FULL_G_MAX_BYTES = 500_000_000   # build_influence() refuses larger matrices unless forced


class Level:
    def __init__(self, name: str, settings: dict | None = None, repo: Path = REPO):
        t0 = time.perf_counter()
        self.name = name
        self.settings = settings if settings is not None else load_settings()
        mat = self.settings["material"]
        self.E0 = float(mat["E_inf_Pa"] + mat["E1_Pa"])      # CONTRACT 4.1: Stage 1 uses E0
        self.nu = float(mat["nu"])
        self.radius = float(self.settings["geometry"]["radius_m"])

        self.surface = load_surface(name, repo)
        self.ns = self.surface["n_surface"]
        self.sn = self.surface["nodes"]
        self.fixed_nodes = self.surface["fixed_nodes"]
        self.fixed_rows = np.searchsorted(self.sn, self.fixed_nodes)

        msh = repo / "model" / name / "ball.msh"
        self.mesh = MeshTet.load(str(msh))
        self._check_mesh_against_files(msh)

        self.basis = Basis(self.mesh, ElementVector(ElementTetP1()))
        self.N = self.basis.N
        # DOF layout must be node-major interleaved (CONTRACT 3); fail loudly otherwise.
        if not np.array_equal(self.basis.nodal_dofs, np.arange(self.N).reshape(-1, 3).T):
            raise RuntimeError("scikit-fem DOF layout is not node-major interleaved; "
                               "a permutation is needed before anything can be exported")
        t1 = time.perf_counter()
        self.K = asm(linear_elasticity(*lame_parameters(self.E0, self.nu)), self.basis).tocsc()
        self.t_assemble = time.perf_counter() - t1

        self.fixed_dofs = (3 * self.fixed_nodes[:, None] + np.arange(3)).ravel()
        self.free = np.setdiff1d(np.arange(self.N), self.fixed_dofs)
        self.red = np.full(self.N, -1, dtype=np.int64)
        self.red[self.free] = np.arange(len(self.free))
        self.surf_dofs = (3 * self.sn[:, None] + np.arange(3)).ravel()   # global DOF of surface DOF
        self.surf_red = self.red[self.surf_dofs]                         # reduced index, -1 if fixed
        self.surf_free = self.surf_red >= 0

        t2 = time.perf_counter()
        self.lu = splu(self.K[self.free][:, self.free].tocsc())
        self.t_factor = time.perf_counter() - t2
        self.t_total = time.perf_counter() - t0

    # ------------------------------------------------------------------ setup checks
    def _check_mesh_against_files(self, msh: Path) -> None:
        s = self.surface
        if self.mesh.p.shape[1] != s["n_nodes"] or self.mesh.t.shape[1] != s["n_tets"]:
            raise RuntimeError(f"{self.name}: scikit-fem node/tet counts differ from surface.json")
        mm = meshio.read(str(msh))
        n_tets = sum(len(c.data) for c in mm.cells if c.type == "tetra")
        if mm.points.shape[0] != self.mesh.p.shape[1] or n_tets != self.mesh.t.shape[1]:
            raise RuntimeError(f"{self.name}: meshio and scikit-fem disagree on counts")
        if not np.allclose(mm.points, self.mesh.p.T, atol=1e-12, rtol=0):
            raise RuntimeError(f"{self.name}: node order/coordinates differ between file and scikit-fem")
        if not np.allclose(self.mesh.p.T[self.sn], s["positions"], atol=1e-12, rtol=0):
            raise RuntimeError(f"{self.name}: surface_positions differ from the mesh")

    # ------------------------------------------------------------------ direct solves
    def _surface_vector(self, F_s) -> np.ndarray:
        F = np.asarray(F_s, dtype=np.float64)
        if F.shape == (self.ns, 3):
            F = F.reshape(-1)
        if F.shape != (3 * self.ns,):
            raise ValueError(f"expected (n_surface, 3) or (3*n_surface,), got {F.shape}")
        return F

    def _solve_reduced(self, rhs_full: np.ndarray) -> np.ndarray:
        """Full-size load vector -> full-size displacement (fixed DOFs stay 0)."""
        u = np.zeros(self.N)
        u[self.free] = self.lu.solve(rhs_full[self.free])
        return u

    def solve_full(self, F_s) -> np.ndarray:
        """Surface loads -> displacement of every node, shape (n_nodes, 3). Loads on fixed rows have no effect."""
        f = np.zeros(self.N)
        f[self.surf_dofs] = self._surface_vector(F_s)
        return self._solve_reduced(f).reshape(-1, 3)

    def solve_loads(self, F_s) -> np.ndarray:
        """Surface loads [N] -> surface displacement (n_surface, 3) [m], one back-substitution."""
        return self.solve_full(F_s)[self.sn]

    # ------------------------------------------------------------------ influence matrix G
    def G_columns(self, cols) -> np.ndarray:
        """Columns of G (CONTRACT 4.3), shape (3*n_surface, len(cols)), float64.
        Column 3*j + b: surface displacement for 1 N in direction b at surface row j.
        Columns of fixed rows are zero, and so are fixed rows of every column."""
        cols = np.asarray(cols, dtype=np.int64).reshape(-1)
        out = np.zeros((3 * self.ns, cols.size))
        live = self.surf_free[cols]
        if live.any():
            lc = cols[live]
            rhs = np.zeros((len(self.free), lc.size))
            rhs[self.surf_red[lc], np.arange(lc.size)] = 1.0
            X = self.lu.solve(rhs)
            tmp = np.zeros((3 * self.ns, lc.size))
            tmp[self.surf_free] = X[self.surf_red[self.surf_free]]
            out[:, live] = tmp
        return out

    def influence_blocks(self, block: int = 256):
        """Yield (col0, G[:, col0:col0+nb]) for all 3*n_surface columns in order, float64.
        Column-major export (CONTRACT 6.5) can write each block as it arrives."""
        n = 3 * self.ns
        for c0 in range(0, n, block):
            yield c0, self.G_columns(np.arange(c0, min(c0 + block, n)))

    def influence_times(self, p, block: int = 256) -> np.ndarray:
        """G @ p as (n_surface, 3), solving only for the nonzero entries of p, in blocks."""
        p = self._surface_vector(p)
        nz = np.flatnonzero(p)
        acc = np.zeros(3 * self.ns)
        for i in range(0, nz.size, block):
            idx = nz[i:i + block]
            acc += self.G_columns(idx) @ p[idx]
        return acc.reshape(self.ns, 3)

    def build_influence(self, force: bool = False) -> np.ndarray:
        """The full G (3*ns x 3*ns, float64). Meant for tests on coarse/medium."""
        nbytes = 8 * (3 * self.ns) ** 2
        if nbytes > FULL_G_MAX_BYTES and not force:
            raise MemoryError(f"full G is {nbytes/1e6:.0f} MB in float64; use influence_blocks() "
                              "or pass force=True")
        G = np.empty((3 * self.ns, 3 * self.ns))
        for c0, blk in self.influence_blocks():
            G[:, c0:c0 + blk.shape[1]] = blk
        return G

    def live_columns(self, p) -> int:
        """Number of nonzero load components on free rows (columns that need a solve)."""
        p = self._surface_vector(p)
        return int(np.count_nonzero(p[self.surf_free]))

    # ------------------------------------------------------------------ independent checks
    def selftest(self, seed: int = 0) -> list[tuple[str, bool, str]]:
        """Checks that do not rely on G agreeing with the direct solve (both share K)."""
        rng = np.random.default_rng(seed)
        res: list[tuple[str, bool, str]] = []

        def add(name, ok, detail):
            res.append((f"{self.name}: {name}", bool(ok), detail))

        # K symmetric
        asym = abs(self.K - self.K.T).max() / abs(self.K).max()
        add("K symmetric", asym < 1e-12, f"{asym:.1e}")

        # rigid-body motions carry no force
        X = self.mesh.p.T
        trans = np.tile([1.0, 0.0, 0.0], len(X))
        rot_z = np.c_[-X[:, 1], X[:, 0], np.zeros(len(X))].ravel()
        rot_x = np.c_[np.zeros(len(X)), -X[:, 2], X[:, 1]].ravel()
        kmax = abs(self.K).max()
        rb = max(abs(self.K @ v).max() for v in (trans, rot_z, rot_x)) / kmax
        add("rigid translation/rotation give zero force", rb < 1e-12, f"{rb:.1e}")

        # reactions + energy for a random load on free surface rows
        free_rows = np.flatnonzero(self.surf_free.reshape(-1, 3).all(axis=1))
        rows = rng.choice(free_rows, size=min(50, free_rows.size), replace=False)
        F = np.zeros((self.ns, 3))
        F[rows] = rng.normal(size=(rows.size, 3))
        F /= np.linalg.norm(F, axis=1).sum()
        f_full = np.zeros(self.N)
        f_full[self.surf_dofs] = F.reshape(-1)
        u = self._solve_reduced(f_full)
        reaction = (self.K @ u)[self.fixed_dofs].reshape(-1, 3).sum(axis=0)
        resid = np.linalg.norm(reaction + F.sum(axis=0))
        add("reactions at fixed nodes balance the applied load", resid < 1e-10, f"|sum| residual {resid:.1e} (applied norm {np.linalg.norm(F.sum(axis=0)):.2f})")
        e1, e2 = 0.5 * f_full @ u, 0.5 * u @ (self.K @ u)
        add("energy identity 1/2 F.u = 1/2 u'Ku", abs(e1 - e2) < 1e-10 * abs(e1), f"{e1:.6e} vs {e2:.6e}")

        # fixed nodes: zero displacement, zero G entries
        us = self.solve_loads(F)
        fz = float(abs(us[self.fixed_rows]).max())
        add("fixed nodes have zero displacement", fz == 0.0, f"{fz:.1e}")
        cols = np.concatenate([3 * self.fixed_rows[:2, None] + np.arange(3),
                               3 * rows[:3, None] + np.arange(3)]).reshape(-1)
        blk = self.G_columns(cols)
        zero_cols = float(abs(blk[:, :6]).max())
        zero_rows = float(abs(blk[(3 * self.fixed_rows[:, None] + np.arange(3)).reshape(-1)]).max())
        add("G: fixed columns and fixed rows are zero", zero_cols == 0.0 and zero_rows == 0.0, f"{zero_cols:.1e}, {zero_rows:.1e}")

        # symmetry of G on a random sub-block (pushing at A, measuring at B == the reverse)
        J = rng.choice(free_rows, size=min(40, free_rows.size), replace=False)
        jc = (3 * J[:, None] + np.arange(3)).reshape(-1)
        sub = self.G_columns(jc)[jc]
        sym = abs(sub - sub.T).max() / abs(sub).max()
        add("G symmetric (sub-block)", sym < 1e-10, f"{sym:.1e}")
        return res


# ---------------------------------------------------------------------- material-law check
def _uniaxial_error(E: float, nu_assembled: float, nu_expected: float, refine: int = 3) -> float:
    """Unit cube under uniform traction s on x=1 (symmetry planes x=0, y=0, z=0):
    exact P1 solution u = (s x/E, -nu s y/E, -nu s z/E). Returns max error / max |u|."""
    m = MeshTet().refined(refine)
    b = Basis(m, ElementVector(ElementTetP1()))
    K = asm(linear_elasticity(*lame_parameters(E, nu_assembled)), b).tocsc()
    P = m.p.T
    fixed = np.unique(np.concatenate([3 * np.flatnonzero(np.isclose(P[:, a], 0)) + a for a in range(3)]))
    free = np.setdiff1d(np.arange(b.N), fixed)
    s = 1000.0
    f = np.zeros(b.N)
    for facet in m.facets[:, m.boundary_facets()].T:
        if np.all(np.isclose(P[facet, 0], 1)):
            area = 0.5 * np.linalg.norm(np.cross(P[facet[1]] - P[facet[0]], P[facet[2]] - P[facet[0]]))
            f[3 * facet] += s * area / 3
    u = np.zeros(b.N)
    u[free] = splu(K[free][:, free].tocsc()).solve(f[free])
    exact = np.c_[s * P[:, 0] / E, -nu_expected * s * P[:, 1] / E, -nu_expected * s * P[:, 2] / E]
    return float(abs(u.reshape(-1, 3) - exact).max() / abs(exact).max())


def selftest_cube(settings: dict | None = None) -> list[tuple[str, bool, str]]:
    settings = settings if settings is not None else load_settings()
    mat = settings["material"]
    E0, nu = float(mat["E_inf_Pa"] + mat["E1_Pa"]), float(mat["nu"])
    err = _uniaxial_error(E0, nu, nu)
    return [("cube: uniaxial analytic solution (E0, nu)", err < 1e-10, f"relative error {err:.1e}")]


def raise_on_failure(results: list[tuple[str, bool, str]]) -> None:
    bad = [r for r in results if not r[1]]
    if bad:
        raise RuntimeError("self-check failed: " + "; ".join(f"{n} ({d})" for n, _, d in bad))
