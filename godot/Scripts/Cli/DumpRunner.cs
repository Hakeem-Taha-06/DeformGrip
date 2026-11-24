using System;
using System.IO;
using System.Runtime.InteropServices;
using DeformGrip.App.Core;
using DeformGrip.App.Data;
using DeformGrip.App.Input;
using DeformGrip.App.Simulation;
using Godot;

namespace DeformGrip.App.Cli
{
    /// <summary>
    /// CLI --dump <level> <case> <profile> implementation (CONTRACT §6.7, godot/README.md Task 7).
    /// Replays <profile> in "ours" mode and writes float32 little-endian [n_t, n_surface, 3]
    /// at dt_out (0.01 s) in contract coordinates (x, y, z) and SI metres (visual gain = 1.0).
    /// </summary>
    public static class DumpRunner
    {
        public static int Run(
            ContractPaths paths,
            string level,
            string caseId,
            string profileId,
            string? outDirOverride = null)
        {
            TestSettings settings = ContractLoader.LoadSettings(paths.SettingsJson);
            SurfaceMeshData surface = ContractLoader.LoadSurface(paths.SurfaceJson(level));
            CaseData caseData = ContractLoader.LoadCase(paths.CaseJson(level, caseId), surface.NSurface);
            ProfileData profile = ContractLoader.LoadProfile(paths.ProfileCsv(profileId), profileId);

            string gMetaPath = paths.GMeta(level);
            string gBinPath = paths.GBin(level);
            if (File.Exists(gMetaPath))
            {
                GMetaData meta = ContractLoader.LoadGMeta(gMetaPath);
                if (meta.NSurface != surface.NSurface)
                    throw new InvalidDataException($"G.meta.json n_surface ({meta.NSurface}) != surface.json ({surface.NSurface}).");
            }

            IDeformCore core = DeformCoreFactory.Load(
                gBinPath,
                surface.NSurface,
                settings.Stage2.Enabled,
                settings.Stage2.Model,
                settings.Material.EInfPa,
                settings.Material.E1Pa,
                settings.Material.Tau1S);

            var forceSource = new CaseForceSource { Mode = ForceInputMode.ProfileReplay };
            forceSource.SetCase(caseData);
            forceSource.SetProfile(profile);

            var runner = new OursRunner(core, settings.Timing.DtForceS);

            double dtForce = settings.Timing.DtForceS;
            double dtOut = settings.Timing.DtOutS;
            int stride = Math.Max(1, (int)Math.Round(dtOut / dtForce));

            string outPath = string.IsNullOrWhiteSpace(outDirOverride)
                ? paths.DumpBin(level, caseId, profileId)
                : Path.Combine(Path.GetFullPath(outDirOverride), $"ours_{level}_{caseId}_{profileId}.bin");

            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            int nSamples = profile.TimesS.Length;
            int nOutFrames = 0;
            float peakDispM = 0f;

            using (var fs = new FileStream(outPath, FileMode.Create, System.IO.FileAccess.Write, FileShare.None, 1 << 20))
            {
                if (!settings.Stage2.Enabled)
                {
                    // Stage 1 elastic: evaluate directly at each dt_out sample (0, dt_out, 2*dt_out, ...)
                    for (int i = 0; i < nSamples; i += stride)
                    {
                        runner.EvaluateCaseAtAmplitude(forceSource, profile.ForcesN[i]);
                        if (runner.MaxDisplacementM > peakDispM)
                            peakDispM = runner.MaxDisplacementM;

                        fs.Write(MemoryMarshal.AsBytes(runner.Displacements.AsSpan()));
                        nOutFrames++;
                    }
                }
                else
                {
                    // Stage 2 viscoelastic: step every dt_force sample and emit every stride (dt_out)
                    for (int i = 0; i < nSamples; i++)
                    {
                        runner.StepFrame(profile.TimesS[i], forceSource);
                        if (i % stride == 0)
                        {
                            if (runner.MaxDisplacementM > peakDispM)
                                peakDispM = runner.MaxDisplacementM;

                            fs.Write(MemoryMarshal.AsBytes(runner.Displacements.AsSpan()));
                            nOutFrames++;
                        }
                    }
                }
            }

            long expectedBytes = 4L * nOutFrames * surface.NSurface * 3;
            long actualBytes = new FileInfo(outPath).Length;
            if (actualBytes != expectedBytes)
            {
                throw new IOException($"Dump byte size mismatch: wrote {actualBytes}, expected {expectedBytes}.");
            }

            GD.Print(
                $"[DumpRunner] Wrote {outPath}\n" +
                $"  shape=[{nOutFrames}, {surface.NSurface}, 3] float32 LE ({actualBytes} bytes), " +
                $"dt_out={dtOut}s, peak_disp={peakDispM * 1000f:F4} mm ({peakDispM:E4} m), core={(core.IsStub ? "STUB" : "REAL")}");
            return 0;
        }
    }
}
