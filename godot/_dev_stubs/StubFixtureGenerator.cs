#if USE_CORE_STUB
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using DeformGrip.App.Data;
using Godot;

namespace DeformGrip.App.DevStubs
{
    /// <summary>
    /// Generates contract-compliant synthetic G.bin, G.meta.json, and SOFA force-table playback files
    /// STRICTLY inside godot/_dev_stubs/fixtures/ (git-ignored) so Role 1b can visually verify
    /// deformation, Side-by-Side mode, --dump, and --benchmark before Role 2b and Role 3 deliver
    /// their real outputs. Defaults to 'coarse' only to keep disk usage tiny (~7.5 MB).
    /// </summary>
    public static class StubFixtureGenerator
    {
        public static string DefaultFixturesRoot(string godotProjectDir)
            => Path.Combine(godotProjectDir, "_dev_stubs", "fixtures");

        public static void Generate(ContractPaths paths, string levelArg)
        {
            TestSettings settings = ContractLoader.LoadSettings(paths.SettingsJson);
            string fixturesRoot = DefaultFixturesRoot(paths.BaseDir);
            string exportRoot = Path.Combine(fixturesRoot, "export");
            string sofaRoot = Path.Combine(fixturesRoot, "sofa_playback");

            var levels = new List<string>();
            if (string.Equals(levelArg, "all", StringComparison.OrdinalIgnoreCase))
            {
                levels.AddRange(new[] { "coarse", "medium", "fine" });
            }
            else
            {
                foreach (string part in levelArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    levels.Add(part);
            }

            if (levels.Count == 0)
                levels.Add("coarse");

            foreach (string level in levels)
            {
                GD.Print($"[StubFixtureGenerator] Building synthetic fixtures for level '{level}' in {fixturesRoot} ...");
                SurfaceMeshData surface = ContractLoader.LoadSurface(paths.SurfaceJson(level));
                float[] gColMajor = BuildSyntheticInfluenceMatrix(surface, settings);

                // Write G.bin + G.meta.json strictly inside _dev_stubs/fixtures/export/<level>/
                string levelExportDir = Path.Combine(exportRoot, level);
                Directory.CreateDirectory(levelExportDir);

                string gBinPath = Path.Combine(levelExportDir, "G.bin");
                using (var fs = new FileStream(gBinPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None, 1 << 20))
                {
                    fs.Write(MemoryMarshal.AsBytes(gColMajor.AsSpan()));
                }

                var meta = new GMetaData
                {
                    MeshLevel = level,
                    NSurface = surface.NSurface,
                    Units = "m per N",
                    E0Pa = settings.Material.E0Pa,
                    Nu = settings.Material.Nu,
                    EInfPa = settings.Material.EInfPa,
                    E1Pa = settings.Material.E1Pa,
                    Tau1S = settings.Material.Tau1S,
                    Layout = "float32 LE, column-major, index = col*3*n_surface + row"
                };
                File.WriteAllText(
                    Path.Combine(levelExportDir, "G.meta.json"),
                    JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }) + "\n");

                GD.Print($"  Wrote {gBinPath} ({new FileInfo(gBinPath).Length / 1e6:F2} MB)");

                // Write synthetic SOFA force tables for every physical case strictly inside _dev_stubs/fixtures/sofa_playback/
                Directory.CreateDirectory(sofaRoot);
                double[] playbackForces = settings.Static.PlaybackTableForcesN.ToArray();
                int nFrames = playbackForces.Length;
                int nSurface = surface.NSurface;
                int nDof = 3 * nSurface;

                foreach (string caseId in settings.Cases.List.Keys)
                {
                    string casePath = paths.CaseJson(level, caseId);
                    if (!File.Exists(casePath))
                        continue;

                    CaseData caseData = ContractLoader.LoadCase(casePath, nSurface);
                    if (!caseData.Physical)
                        continue;

                    // Compute unit-amplitude displacement u1 = G * p
                    float[] u1 = new float[nDof];
                    foreach (CaseLoadEntry ld in caseData.Loads)
                    {
                        int baseCol = 3 * ld.Row;
                        for (int c = 0; c < 3; c++)
                        {
                            float fc = (float)ld.F[c];
                            if (fc == 0f) continue;
                            int colOff = (baseCol + c) * nDof;
                            for (int r = 0; r < nDof; r++)
                                u1[r] += fc * gColMajor[colOff + r];
                        }
                    }

                    // Build [nFrames, nSurface, 3] float32 LE playback table
                    float[] table = new float[nFrames * nDof];
                    for (int fi = 0; fi < nFrames; fi++)
                    {
                        float amp = (float)playbackForces[fi];
                        int dstOff = fi * nDof;
                        for (int r = 0; r < nDof; r++)
                            table[dstOff + r] = amp * u1[r];
                    }

                    string binOut = Path.Combine(sofaRoot, $"{level}_{caseId}_forcetable.bin");
                    using (var fs = new FileStream(binOut, FileMode.Create, System.IO.FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        fs.Write(MemoryMarshal.AsBytes(table.AsSpan()));
                    }

                    var pbMeta = new SofaPlaybackMeta
                    {
                        Type = "force_table",
                        CaseId = caseId,
                        ForcesN = playbackForces,
                        NSurface = nSurface
                    };
                    string jsonOut = Path.Combine(sofaRoot, $"{level}_{caseId}_forcetable.json");
                    File.WriteAllText(
                        jsonOut,
                        JsonSerializer.Serialize(pbMeta, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                }

                GD.Print($"  Wrote synthetic SOFA force tables for level '{level}' into {sofaRoot}");
            }

            GD.Print("[StubFixtureGenerator] Done. All synthetic fixtures are strictly inside godot/_dev_stubs/fixtures/ (git-ignored).");
        }

        /// <summary>
        /// Builds a symmetric positive-definite compliance matrix G (column-major float32)
        /// with exact zero rows/columns on fixed nodes and ~0.15 mm/N peak compliance (matching CONTRACT §9 sanity scale).
        /// </summary>
        private static float[] BuildSyntheticInfluenceMatrix(SurfaceMeshData surface, TestSettings settings)
        {
            int n = surface.NSurface;
            int nDof = 3 * n;
            float[] g = new float[(long)nDof * nDof];

            double r = surface.RadiusM;
            double sigma = 0.55 * r;
            double invTwoSigmaSq = 1.0 / (2.0 * sigma * sigma);

            // Smooth boundary taper so displacement smoothly vanishes at the fixed -z cap
            var fixedSet = new HashSet<int>(surface.FixedNodes);
            double[] freeWeight = new double[n];
            double zFixed = -r * Math.Cos(settings.Geometry.FixedCapDeg * Math.PI / 180.0);
            for (int i = 0; i < n; i++)
            {
                if (fixedSet.Contains(surface.SurfaceNodes[i]))
                {
                    freeWeight[i] = 0.0;
                }
                else
                {
                    double z = surface.SurfacePositions[i][2];
                    double t = Math.Clamp((z - zFixed) / (0.6 * r), 0.0, 1.0);
                    freeWeight[i] = t * t * (3.0 - 2.0 * t); // smoothstep
                }
            }

            // Representative compliance: axial ~1.8e-4 m/N (10 N cap_medium -> ~1.4 mm),
            // lateral shear/bending ~1.25e-3 m/N (matching Role 2a's cap_shear: < 3.5 mm at 1 N, > 3.5 mm at 5 N).
            double complianceZ = 1.8e-4;
            double complianceXY = 1.25e-3;

            for (int j = 0; j < n; j++)
            {
                double wj = freeWeight[j];
                if (wj == 0.0)
                    continue;

                double[] pj = surface.SurfacePositions[j];
                int colX = (3 * j) * nDof;
                int colY = (3 * j + 1) * nDof;
                int colZ = (3 * j + 2) * nDof;

                for (int i = 0; i < n; i++)
                {
                    double wi = freeWeight[i];
                    if (wi == 0.0)
                        continue;

                    double[] pi = surface.SurfacePositions[i];
                    double dx = pi[0] - pj[0];
                    double dy = pi[1] - pj[1];
                    double dz = pi[2] - pj[2];
                    double distSq = dx * dx + dy * dy + dz * dz;

                    double kernel = wi * wj * Math.Exp(-distSq * invTwoSigmaSq);
                    float kXY = (float)(complianceXY * kernel);
                    float kZ = (float)(complianceZ * kernel);
                    int rBase = 3 * i;
                    g[colX + rBase] = kXY;
                    g[colY + rBase + 1] = kXY;
                    g[colZ + rBase + 2] = kZ;
                }
            }

            return g;
        }
    }
}
#endif
