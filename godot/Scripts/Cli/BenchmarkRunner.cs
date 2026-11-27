using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeformGrip.App.Core;
using DeformGrip.App.Data;
using DeformGrip.App.Input;
using DeformGrip.App.Scene;
using DeformGrip.App.Simulation;
using Godot;

namespace DeformGrip.App.Cli
{
    /// <summary>
    /// Live-rendering CLI --benchmark state machine (CONTRACT §6.6, godot/README.md Task 8).
    /// Forces VSync OFF and uncapped FPS, replays slow_squeeze across levels x timing_cases,
    /// and writes two separate measurements per JSON: (1) core update only, (2) whole frame.
    /// </summary>
    public sealed class BenchmarkRunner
    {
        private readonly ContractPaths _paths;
        private readonly BallMeshView _ballView;
        private readonly TestSettings _settings;
        private readonly ProfileData _profile;
        private readonly string? _outDirOverride;
        private readonly int _warmupFrames;
        private readonly Queue<(string Level, string CaseId)> _jobs = new();

        private string _currentLevel = "";
        private string _currentCaseId = "";
        private string _loadedLevel = "";
        private SurfaceMeshData? _surface;
        private IDeformCore? _core;
        private OursRunner? _oursRunner;
        private CaseForceSource? _forceSource;

        private int _warmupRemaining;
        private double _simTimeS;
        private long _prevFrameTicks;
        private readonly List<double> _coreUpdateMs = new(4096);
        private readonly List<double> _wholeFrameMs = new(4096);
        private readonly List<string> _summaryLines = new();

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        public bool IsFinished { get; private set; }
        public int ExitCode { get; private set; }

        public BenchmarkRunner(
            ContractPaths paths,
            BallMeshView ballView,
            IEnumerable<string>? levelsOverride,
            string? outDirOverride,
            int warmupFrames)
        {
            _paths = paths;
            _ballView = ballView;
            _outDirOverride = outDirOverride;
            _warmupFrames = Math.Max(0, warmupFrames);
            _settings = ContractLoader.LoadSettings(paths.SettingsJson);
            _profile = ContractLoader.LoadProfile(paths.ProfileCsv("slow_squeeze"), "slow_squeeze");

            // Force VSync OFF and uncapped FPS as required by godot/README.md Task 8
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = 0;

            if (OS.HasFeature("editor"))
            {
                GD.PushWarning(
                    "[BenchmarkRunner] Running inside the Godot editor. Final timing files for the report " +
                    "MUST be generated from an exported release build (not the editor) with VSync off.");
            }

            List<string> levels = levelsOverride != null && levelsOverride.Any()
                ? levelsOverride.ToList()
                : new List<string> { "coarse", "medium", "fine" };

            foreach (string level in levels)
            {
                foreach (string caseId in _settings.TimingCases)
                {
                    _jobs.Enqueue((level, caseId));
                }
            }

            GD.Print($"[BenchmarkRunner] Queued {_jobs.Count} timing jobs (profile=slow_squeeze, duration={_profile.DurationS:F2}s, warmup={_warmupFrames} frames, VSync=Disabled).");
            StartNextJob();
        }

        public void TickFrame()
        {
            if (IsFinished)
                return;

            long nowTicks = Stopwatch.GetTimestamp();
            double frameIntervalMs = (_prevFrameTicks > 0) ? (nowTicks - _prevFrameTicks) * TicksToMs : 0.0;
            _prevFrameTicks = nowTicks;

            if (_oursRunner == null || _forceSource == null)
            {
                IsFinished = true;
                ExitCode = 1;
                return;
            }

            if (_warmupRemaining > 0)
            {
                // Warm up at mid-profile peak load so JIT, GPU VBOs, and shaders stabilize
                _oursRunner.StepFrame(_profile.DurationS * 0.5, _forceSource);
                _ballView.ApplyDisplacements(_oursRunner.Displacements, 1.0f);
                _warmupRemaining--;
                if (_warmupRemaining == 0)
                {
                    _oursRunner.Reset();
                    _simTimeS = 0.0;
                    _prevFrameTicks = Stopwatch.GetTimestamp();
                }
                return;
            }

            // Record whole-frame wall-clock interval (previous frame start -> this frame start)
            if (frameIntervalMs > 0.0)
                _wholeFrameMs.Add(frameIntervalMs);

            // Advance replay time by real wall-clock delta (capped to avoid skipping entire profile on a stall)
            double dtSec = Math.Clamp(frameIntervalMs / 1000.0, 1e-6, 0.05);
            _simTimeS += dtSec;

            // Time core update ONLY (load-set build + InfluenceMatrix.Apply / Stage 2 step)
            long coreStart = Stopwatch.GetTimestamp();
            _oursRunner.StepFrame(Math.Min(_simTimeS, _profile.DurationS), _forceSource);
            long coreEnd = Stopwatch.GetTimestamp();
            _coreUpdateMs.Add((coreEnd - coreStart) * TicksToMs);

            // Apply displacements & recompute normals + upload VBO (part of whole frame)
            _ballView.ApplyDisplacements(_oursRunner.Displacements, 1.0f);

            // Finish job once full slow_squeeze profile has been replayed (and at least 120 samples collected)
            if (_simTimeS >= _profile.DurationS && _wholeFrameMs.Count >= 120)
            {
                FinishCurrentJob();
                StartNextJob();
            }
        }

        private void StartNextJob()
        {
            if (_jobs.Count == 0)
            {
                GD.Print("\n=== DeformGrip Benchmark Summary (target frame_ms_max = " +
                         $"{_settings.Targets.FrameMsMax:F1} ms) ===");
                foreach (string line in _summaryLines)
                    GD.Print(line);
                IsFinished = true;
                ExitCode = 0;
                return;
            }

            (_currentLevel, _currentCaseId) = _jobs.Dequeue();
            try
            {
                if (_currentLevel != _loadedLevel)
                {
                    _surface = ContractLoader.LoadSurface(_paths.SurfaceJson(_currentLevel));
                    _ballView.InitializeMesh(_surface, new Color(0.87f, 0.48f, 0.34f), new Color(0.22f, 0.25f, 0.32f));
                    _core = DeformCoreFactory.Load(
                        _paths.GBin(_currentLevel),
                        _surface.NSurface,
                        _settings.Stage2.Enabled,
                        _settings.Stage2.Model,
                        _settings.Material.EInfPa,
                        _settings.Material.E1Pa,
                        _settings.Material.Tau1S);
                    _oursRunner = new OursRunner(_core, _settings.Timing.DtForceS);
                    _loadedLevel = _currentLevel;
                }

                CaseData caseData = ContractLoader.LoadCase(_paths.CaseJson(_currentLevel, _currentCaseId), _surface!.NSurface);
                _forceSource = new CaseForceSource { Mode = ForceInputMode.ProfileReplay };
                _forceSource.SetCase(caseData);
                _forceSource.SetProfile(_profile);
                _oursRunner!.Reset();

                _coreUpdateMs.Clear();
                _wholeFrameMs.Clear();
                _warmupRemaining = _warmupFrames;
                _simTimeS = 0.0;
                _prevFrameTicks = 0;

                GD.Print($"[BenchmarkRunner] Starting {_currentLevel} / {_currentCaseId} (n_surface={_surface.NSurface}, n_loads={caseData.Loads.Length}, update={_ballView.ActiveUpdateMethod})...");
            }
            catch (Exception ex)
            {
                GD.PushError($"[BenchmarkRunner] Failed to initialize {_currentLevel}/{_currentCaseId}: {ex.Message}");
                IsFinished = true;
                ExitCode = 1;
            }
        }

        private void FinishCurrentJob()
        {
            Measurement coreStats = ComputeStats(
                $"Godot C# core update only (load-set build + InfluenceMatrix.Apply; slow_squeeze replay, {_warmupFrames}-frame warmup excluded)",
                _coreUpdateMs);
            Measurement frameStats = ComputeStats(
                $"Godot whole frame wall-clock interval (core update + normal recompute + GPU VBO upload + GL Compatibility render, VSync off; slow_squeeze replay, {_warmupFrames}-frame warmup excluded)",
                _wholeFrameMs);

            string outPath = string.IsNullOrWhiteSpace(_outDirOverride)
                ? _paths.TimingJson(_currentLevel, _currentCaseId)
                : Path.Combine(Path.GetFullPath(_outDirOverride), $"{_currentLevel}_{_currentCaseId}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            string buildMode = OS.HasFeature("editor") ? "editor" : "exported build";
            string coreKind = DeformCoreFactory.IsUsingStub ? "STUB core" : "real core";
            double budget = _settings.Targets.FrameMsMax;

            var report = new TimingReport
            {
                Hardware = DetectHardwareSummary(),
                Software = $"Godot {_paths.ReadPinnedVersion()} (.NET 8, GL Compatibility, {OS.GetName()} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, {buildMode}, VSync off, {coreKind})",
                MeshLevel = _currentLevel,
                CaseId = _currentCaseId,
                Profile = _profile.ProfileId,
                TargetFrameMsMax = budget,
                MeetsFrameTargetMean = frameStats.MsMean <= budget,
                MeetsFrameTargetP95 = frameStats.MsP95 <= budget,
                Measurements = new[] { coreStats, frameStats }
            };

            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }) + "\n";
            File.WriteAllText(outPath, json);

            string passStr = frameStats.MsP95 <= budget ? "PASS (p95 <= budget)" : "OVER BUDGET";
            string summary =
                $"  {_currentLevel,-7} | {_currentCaseId,-13} | core mean/p95/max = {coreStats.MsMean:F3}/{coreStats.MsP95:F3}/{coreStats.MsMax:F3} ms | " +
                $"frame mean/p95/max = {frameStats.MsMean:F3}/{frameStats.MsP95:F3}/{frameStats.MsMax:F3} ms (n={frameStats.NSamples}) | {passStr} -> {outPath}";
            _summaryLines.Add(summary);
            GD.Print(summary);
        }

        private static Measurement ComputeStats(string whatIsTimed, List<double> samples)
        {
            if (samples.Count == 0)
            {
                return new Measurement { WhatIsTimed = whatIsTimed };
            }

            double[] sorted = samples.ToArray();
            Array.Sort(sorted);

            double sum = 0.0;
            for (int i = 0; i < sorted.Length; i++)
                sum += sorted[i];

            double mean = sum / sorted.Length;
            double max = sorted[^1];
            double p95 = Percentile(sorted, 0.95);

            return new Measurement
            {
                WhatIsTimed = whatIsTimed,
                NSamples = sorted.Length,
                MsMean = Math.Round(mean, 4),
                MsP95 = Math.Round(p95, 4),
                MsMax = Math.Round(max, 4)
            };
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 1) return sorted[0];
            double idx = p * (sorted.Length - 1);
            int lo = (int)Math.Floor(idx);
            int hi = Math.Min(sorted.Length - 1, lo + 1);
            double frac = idx - lo;
            return sorted[lo] + frac * (sorted[hi] - sorted[lo]);
        }

        private static string DetectHardwareSummary()
        {
            string cpu = OS.GetProcessorName().Trim();
            if (string.IsNullOrEmpty(cpu) && File.Exists("/proc/cpuinfo"))
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    {
                        int colon = line.IndexOf(':');
                        if (colon >= 0)
                        {
                            cpu = line[(colon + 1)..].Trim();
                            break;
                        }
                    }
                }
            }

            int cores = OS.GetProcessorCount();
            string gpu = RenderingServer.GetVideoAdapterName();
            string os = $"{OS.GetName()} {OS.GetVersion()}";
            return $"{cpu} ({cores} logical CPUs); GPU: {gpu}; OS: {os}";
        }

        private sealed class TimingReport
        {
            [JsonPropertyName("hardware")] public string Hardware { get; set; } = "";
            [JsonPropertyName("software")] public string Software { get; set; } = "";
            [JsonPropertyName("mesh_level")] public string MeshLevel { get; set; } = "";
            [JsonPropertyName("case_id")] public string CaseId { get; set; } = "";
            [JsonPropertyName("profile")] public string Profile { get; set; } = "";
            [JsonPropertyName("target_frame_ms_max")] public double TargetFrameMsMax { get; set; }
            [JsonPropertyName("meets_frame_target_mean")] public bool MeetsFrameTargetMean { get; set; }
            [JsonPropertyName("meets_frame_target_p95")] public bool MeetsFrameTargetP95 { get; set; }
            [JsonPropertyName("measurements")] public Measurement[] Measurements { get; set; } = Array.Empty<Measurement>();
        }

        private sealed class Measurement
        {
            [JsonPropertyName("what_is_timed")] public string WhatIsTimed { get; set; } = "";
            [JsonPropertyName("n_samples")] public int NSamples { get; set; }
            [JsonPropertyName("ms_mean")] public double MsMean { get; set; }
            [JsonPropertyName("ms_p95")] public double MsP95 { get; set; }
            [JsonPropertyName("ms_max")] public double MsMax { get; set; }
        }
    }
}
