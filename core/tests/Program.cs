using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeformGrip.Core;

namespace DeformGrip.Core.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string repoRoot = FindRepoRoot();
            string? evalOutDir = null;
            string? viscoOutDir = null;
            bool runSelfTest = true;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--repo":
                        if (i + 1 >= args.Length) return Fail("Missing path after --repo");
                        repoRoot = Path.GetFullPath(args[++i]);
                        break;
                    case "--eval-cases":
                        if (i + 1 >= args.Length) return Fail("Missing dir after --eval-cases");
                        evalOutDir = Path.GetFullPath(args[++i]);
                        break;
                    case "--eval-visco":
                        if (i + 1 >= args.Length) return Fail("Missing dir after --eval-visco");
                        viscoOutDir = Path.GetFullPath(args[++i]);
                        break;
                    case "--no-selftest":
                        runSelfTest = false;
                        break;
                    default:
                        return Fail($"Unknown argument: {args[i]}");
                }
            }

            if (runSelfTest)
            {
                RunViscoelasticSelfTests();
                RunInfluenceMatrixSummary(repoRoot);
            }

            if (!string.IsNullOrEmpty(evalOutDir))
            {
                ExportCaseEvaluations(repoRoot, evalOutDir);
            }

            if (!string.IsNullOrEmpty(viscoOutDir))
            {
                ExportViscoProfile(repoRoot, viscoOutDir);
            }

            return 0;
        }

        private static int Fail(string msg)
        {
            Console.Error.WriteLine($"ERROR: {msg}");
            return 1;
        }

        private static string FindRepoRoot()
        {
            DirectoryInfo? dir = new(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CONTRACT.md")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CONTRACT.md")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate DeformGrip repository root containing CONTRACT.md.");
        }

        private static void RunViscoelasticSelfTests()
        {
            const double eInf = 300000.0;
            const double e1 = 200000.0;
            const double e0 = eInf + e1;
            const double tau1 = 0.5;
            const double dt = 0.001;

            // 1. Step force: f_eff(0+) == f
            var sls = new ViscoelasticState(1, eInf, e1, tau1, model: "sls", elasticMode: false);
            double[] f0 = { 10.0, -5.0, 2.5 };
            double[] fZero = { 0.0, 0.0, 0.0 };
            ReadOnlySpan<double> fEff0 = sls.Step(f0, f0, 0.0);
            for (int d = 0; d < 3; d++)
            {
                if (Math.Abs(fEff0[d] - f0[d]) > 1e-12)
                    throw new Exception($"Step f_eff(0+) mismatch at dof {d}: {fEff0[d]} vs {f0[d]}");
            }

            // 2. Constant force tends to f * E0 / E_inf
            for (int step = 0; step < 20000; step++)
            {
                sls.Step(f0, f0, dt);
            }
            ReadOnlySpan<double> steady = sls.Step(f0, f0, dt);
            double steady0 = steady[0];
            for (int d = 0; d < 3; d++)
            {
                double expected = f0[d] * (e0 / eInf);
                double rel = Math.Abs(steady[d] - expected) / Math.Abs(expected);
                if (rel > 1e-8)
                    throw new Exception($"Steady-state limit mismatch at dof {d}: {steady[d]} vs {expected}");
            }

            // 3. Release to zero: keeps relaxing smoothly (> 0 for positive load) toward 0
            ReadOnlySpan<double> afterDrop = sls.Step(fZero, fZero, dt);
            if (!(afterDrop[0] > 0.0 && afterDrop[0] < steady0))
                throw new Exception($"Post-release relaxation failed: {afterDrop[0]} vs steady {steady0}");
            for (int step = 0; step < 20000; step++)
            {
                sls.Step(fZero, fZero, dt);
            }
            ReadOnlySpan<double> relaxed = sls.Step(fZero, fZero, dt);
            if (Math.Abs(relaxed[0]) > 1e-8)
                throw new Exception($"Did not relax back to zero: {relaxed[0]}");

            // 4. Agreement with high-resolution RK4 ODE integration for a linear ramp
            sls.Reset();
            double pOde = 0.0;
            double fStart = 0.0;
            double slope = 10.0 / 3.0; // 0 -> 10 N over 3 s
            int nCoarseSteps = 1000;
            int subSteps = 200;
            double subDt = dt / subSteps;
            double maxErr = 0.0;
            for (int k = 0; k < nCoarseSteps; k++)
            {
                double tPrev = k * dt;
                double tNext = (k + 1) * dt;
                double[] fp = { fStart + slope * tPrev, 0.0, 0.0 };
                double[] fn = { fStart + slope * tNext, 0.0, 0.0 };
                ReadOnlySpan<double> got = sls.Step(fp, fn, dt);

                for (int s = 0; s < subSteps; s++)
                {
                    double ts = tPrev + s * subDt;
                    double k1 = ((fStart + slope * ts) - (eInf / e0) * pOde) / tau1;
                    double k2 = ((fStart + slope * (ts + 0.5 * subDt)) - (eInf / e0) * (pOde + 0.5 * subDt * k1)) / tau1;
                    double k3 = ((fStart + slope * (ts + 0.5 * subDt)) - (eInf / e0) * (pOde + 0.5 * subDt * k2)) / tau1;
                    double k4 = ((fStart + slope * (ts + subDt)) - (eInf / e0) * (pOde + subDt * k3)) / tau1;
                    pOde += (subDt / 6.0) * (k1 + 2.0 * k2 + 2.0 * k3 + k4);
                }
                double refFEff = fn[0] + (e1 / e0) * pOde;
                maxErr = Math.Max(maxErr, Math.Abs(got[0] - refFEff));
            }
            if (maxErr > 1e-8)
                throw new Exception($"SLS ODE ramp error too large: {maxErr:E3}");

            // 5. Elastic mode: effective force equals input
            sls.Reset();
            sls.ElasticMode = true;
            ReadOnlySpan<double> elasticOut = sls.Step(fZero, f0, dt);
            for (int d = 0; d < 3; d++)
            {
                if (elasticOut[d] != f0[d])
                    throw new Exception($"Elastic mode mismatch at dof {d}: {elasticOut[d]} vs {f0[d]}");
            }

            Console.WriteLine($"PASS  ViscoelasticState self-tests (RK4 ODE ramp max abs error = {maxErr:E2})");
        }

        private static void RunInfluenceMatrixSummary(string repoRoot)
        {
            string[] levels = { "coarse", "medium", "fine" };
            foreach (string lvl in levels)
            {
                string gBin = Path.Combine(repoRoot, "solver", "export", lvl, "G.bin");
                string gMeta = Path.Combine(repoRoot, "solver", "export", lvl, "G.meta.json");
                string casePath = Path.Combine(repoRoot, "config", "cases", lvl, "cap_medium.json");
                if (!File.Exists(gBin) || !File.Exists(gMeta) || !File.Exists(casePath))
                {
                    Console.WriteLine($"SKIP  {lvl}: missing G.bin / G.meta.json / cap_medium.json");
                    continue;
                }

                var meta = JsonSerializer.Deserialize<GMetaJson>(File.ReadAllText(gMeta))!;
                var caseData = JsonSerializer.Deserialize<CaseJson>(File.ReadAllText(casePath))!;

                var matrix = InfluenceMatrix.Load(gBin, meta.NSurface);
                var loads = ToNodalLoads(caseData.Loads, amplitudeN: 10.0);
                var outDisp = new float[matrix.NDof];
                matrix.Apply(loads, 1.0f, outDisp);

                double peak = MaxDispNorm(outDisp, meta.NSurface);
                Console.WriteLine(
                    $"PASS  InfluenceMatrix {lvl,-6} cap_medium @ 10 N: " +
                    $"n_surface={meta.NSurface}, peak_disp={peak * 1000.0:F4} mm ({peak:E6} m)");
            }
        }

        private static void ExportCaseEvaluations(string repoRoot, string outDir)
        {
            Directory.CreateDirectory(outDir);
            string[] levels = { "coarse", "medium", "fine" };
            foreach (string lvl in levels)
            {
                string gBin = Path.Combine(repoRoot, "solver", "export", lvl, "G.bin");
                string gMeta = Path.Combine(repoRoot, "solver", "export", lvl, "G.meta.json");
                string casesDir = Path.Combine(repoRoot, "config", "cases", lvl);
                if (!File.Exists(gBin) || !File.Exists(gMeta) || !Directory.Exists(casesDir))
                    continue;

                var meta = JsonSerializer.Deserialize<GMetaJson>(File.ReadAllText(gMeta))!;
                var matrix = InfluenceMatrix.Load(gBin, meta.NSurface);
                var outDisp = new float[matrix.NDof];

                foreach (string caseFile in Directory.GetFiles(casesDir, "*.json"))
                {
                    var caseData = JsonSerializer.Deserialize<CaseJson>(File.ReadAllText(caseFile))!;
                    var loads = ToNodalLoads(caseData.Loads, amplitudeN: 10.0);
                    matrix.Apply(loads, 1.0f, outDisp);

                    string outPath = Path.Combine(outDir, $"cs_{lvl}_{caseData.CaseId}_10N.bin");
                    File.WriteAllBytes(outPath, MemoryMarshal.AsBytes(outDisp.AsSpan()).ToArray());
                }
            }
            Console.WriteLine($"Exported C# 10 N case evaluations to {outDir}");
        }

        private static void ExportViscoProfile(string repoRoot, string outDir)
        {
            Directory.CreateDirectory(outDir);
            string lvl = "medium";
            string cid = "cap_medium";
            string pid = "slow_squeeze";

            string gBin = Path.Combine(repoRoot, "solver", "export", lvl, "G.bin");
            string gMeta = Path.Combine(repoRoot, "solver", "export", lvl, "G.meta.json");
            string casePath = Path.Combine(repoRoot, "config", "cases", lvl, $"{cid}.json");
            string profilePath = Path.Combine(repoRoot, "config", "profiles", $"{pid}.csv");
            if (!File.Exists(gBin) || !File.Exists(gMeta) || !File.Exists(casePath) || !File.Exists(profilePath))
                throw new FileNotFoundException("Missing data files for --eval-visco.");

            var meta = JsonSerializer.Deserialize<GMetaJson>(File.ReadAllText(gMeta))!;
            var caseData = JsonSerializer.Deserialize<CaseJson>(File.ReadAllText(casePath))!;
            var (times, forces) = LoadProfile(profilePath);

            var matrix = InfluenceMatrix.Load(gBin, meta.NSurface);
            var visco = new ViscoelasticState(meta.NSurface, meta.EInfPa, meta.E1Pa, meta.Tau1S, "sls", elasticMode: false);

            int nDof = matrix.NDof;
            double[] fPrev = new double[nDof];
            double[] fNext = new double[nDof];
            var loadBuf = new NodalLoad[meta.NSurface];
            float[] disp = new float[nDof];

            string outPath = Path.Combine(outDir, $"cs_visco_{lvl}_{cid}_{pid}.bin");
            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            int stride = 10; // dt_out / dt_force = 0.01 / 0.001

            for (int i = 0; i < times.Length; i++)
            {
                FillDenseCase(caseData.Loads, forces[i], fNext);
                double dt = (i == 0) ? 0.0 : (times[i] - times[i - 1]);
                ReadOnlySpan<double> fEff = visco.Step(i == 0 ? fNext : fPrev, fNext, dt);
                Array.Copy(fNext, fPrev, nDof);

                if (i % stride == 0)
                {
                    int nLoads = 0;
                    for (int row = 0; row < meta.NSurface; row++)
                    {
                        double fx = fEff[3 * row];
                        double fy = fEff[3 * row + 1];
                        double fz = fEff[3 * row + 2];
                        if (fx != 0.0 || fy != 0.0 || fz != 0.0)
                            loadBuf[nLoads++] = new NodalLoad(row, fx, fy, fz);
                    }
                    matrix.Apply(loadBuf.AsSpan(0, nLoads), 1.0f, disp);
                    fs.Write(MemoryMarshal.AsBytes(disp.AsSpan()));
                }
            }
            Console.WriteLine($"Exported C# viscoelastic replay to {outPath}");
        }

        private static NodalLoad[] ToNodalLoads(CaseLoadEntry[] entries, double amplitudeN)
        {
            var loads = new NodalLoad[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                loads[i] = new NodalLoad(
                    entries[i].Row,
                    amplitudeN * entries[i].F[0],
                    amplitudeN * entries[i].F[1],
                    amplitudeN * entries[i].F[2]);
            }
            return loads;
        }

        private static void FillDenseCase(CaseLoadEntry[] entries, double amplitudeN, double[] dense)
        {
            Array.Clear(dense, 0, dense.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                int b = 3 * entries[i].Row;
                dense[b] = amplitudeN * entries[i].F[0];
                dense[b + 1] = amplitudeN * entries[i].F[1];
                dense[b + 2] = amplitudeN * entries[i].F[2];
            }
        }

        private static double MaxDispNorm(float[] disp, int nSurface)
        {
            double maxSq = 0.0;
            for (int i = 0; i < nSurface; i++)
            {
                double x = disp[3 * i];
                double y = disp[3 * i + 1];
                double z = disp[3 * i + 2];
                double sq = x * x + y * y + z * z;
                if (sq > maxSq) maxSq = sq;
            }
            return Math.Sqrt(maxSq);
        }

        private static (double[] Times, double[] Forces) LoadProfile(string path)
        {
            var times = new List<double>(8192);
            var forces = new List<double>(8192);
            using var sr = new StreamReader(path);
            sr.ReadLine(); // header
            while (sr.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int comma = line.IndexOf(',');
                times.Add(double.Parse(line.AsSpan(0, comma), CultureInfo.InvariantCulture));
                forces.Add(double.Parse(line.AsSpan(comma + 1), CultureInfo.InvariantCulture));
            }
            return (times.ToArray(), forces.ToArray());
        }

        private sealed class GMetaJson
        {
            [JsonPropertyName("n_surface")] public int NSurface { get; set; }
            [JsonPropertyName("E_inf_Pa")] public double EInfPa { get; set; }
            [JsonPropertyName("E1_Pa")] public double E1Pa { get; set; }
            [JsonPropertyName("tau1_s")] public double Tau1S { get; set; }
        }

        private sealed class CaseJson
        {
            [JsonPropertyName("case_id")] public string CaseId { get; set; } = "";
            [JsonPropertyName("loads")] public CaseLoadEntry[] Loads { get; set; } = Array.Empty<CaseLoadEntry>();
        }

        private sealed class CaseLoadEntry
        {
            [JsonPropertyName("row")] public int Row { get; set; }
            [JsonPropertyName("f")] public double[] F { get; set; } = Array.Empty<double>();
        }
    }
}
