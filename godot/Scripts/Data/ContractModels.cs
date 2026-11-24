using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DeformGrip.App.Data
{
    public sealed class TestSettings
    {
        [JsonPropertyName("geometry")] public GeometrySettings Geometry { get; set; } = new();
        [JsonPropertyName("validity")] public ValiditySettings? Validity { get; set; }
        [JsonPropertyName("mesh_levels")] public Dictionary<string, int> MeshLevels { get; set; } = new();
        [JsonPropertyName("material")] public MaterialSettings Material { get; set; } = new();
        [JsonPropertyName("timing")] public TimingSettings Timing { get; set; } = new();
        [JsonPropertyName("static")] public StaticSettings Static { get; set; } = new();
        [JsonPropertyName("cases")] public CasesSettings Cases { get; set; } = new();
        [JsonPropertyName("timing_cases")] public List<string> TimingCases { get; set; } = new();
        [JsonPropertyName("profiles")] public Dictionary<string, ProfileSpec> Profiles { get; set; } = new();
        [JsonPropertyName("stage2")] public Stage2Settings Stage2 { get; set; } = new();
        [JsonPropertyName("targets")] public TargetSettings Targets { get; set; } = new();
    }

    public sealed class GeometrySettings
    {
        [JsonPropertyName("radius_m")] public double RadiusM { get; set; } = 0.035;
        [JsonPropertyName("fixed_cap_deg")] public double FixedCapDeg { get; set; } = 30.0;
    }

    public sealed class ValiditySettings
    {
        [JsonPropertyName("max_disp_fraction_of_radius")] public double MaxDispFractionOfRadius { get; set; }
    }

    public sealed class MaterialSettings
    {
        [JsonPropertyName("nu")] public double Nu { get; set; } = 0.35;
        [JsonPropertyName("E_inf_Pa")] public double EInfPa { get; set; } = 300000.0;
        [JsonPropertyName("E1_Pa")] public double E1Pa { get; set; } = 200000.0;
        [JsonPropertyName("tau1_s")] public double Tau1S { get; set; } = 0.5;
        [JsonPropertyName("density_kg_m3")] public double DensityKgM3 { get; set; } = 1000.0;
        public double E0Pa => EInfPa + E1Pa;
    }

    public sealed class TimingSettings
    {
        [JsonPropertyName("dt_force_s")] public double DtForceS { get; set; } = 0.001;
        [JsonPropertyName("dt_out_s")] public double DtOutS { get; set; } = 0.01;
    }

    public sealed class StaticSettings
    {
        [JsonPropertyName("forces_N")] public List<double> ForcesN { get; set; } = new();
        [JsonPropertyName("nonlinear_check_forces_N")] public List<double> NonlinearCheckForcesN { get; set; } = new();
        [JsonPropertyName("playback_table_forces_N")] public List<double> PlaybackTableForcesN { get; set; } = new();
    }

    public sealed class CasesSettings
    {
        [JsonPropertyName("min_nodes")] public int MinNodes { get; set; } = 5;
        [JsonPropertyName("list")] public Dictionary<string, object> List { get; set; } = new();
    }

    public sealed class ProfileSpec
    {
        [JsonPropertyName("ramp_s")] public double RampS { get; set; }
        [JsonPropertyName("peak_N")] public double PeakN { get; set; }
        [JsonPropertyName("hold_s")] public double HoldS { get; set; }
        [JsonPropertyName("release_s")] public double ReleaseS { get; set; }
        [JsonPropertyName("tail_s")] public double TailS { get; set; }
    }

    public sealed class Stage2Settings
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("model")] public string Model { get; set; } = "sls";
        [JsonPropertyName("levels")] public List<string> Levels { get; set; } = new();
        [JsonPropertyName("cases")] public List<string> Cases { get; set; } = new();
    }

    public sealed class TargetSettings
    {
        [JsonPropertyName("rel_L2_max")] public double RelL2Max { get; set; } = 0.05;
        [JsonPropertyName("frame_ms_max")] public double FrameMsMax { get; set; } = 16.7;
    }

    public sealed class SurfaceMeshData
    {
        [JsonPropertyName("mesh_level")] public string MeshLevel { get; set; } = "";
        [JsonPropertyName("radius_m")] public double RadiusM { get; set; } = 0.035;
        [JsonPropertyName("n_nodes")] public int NNodes { get; set; }
        [JsonPropertyName("n_tets")] public int NTets { get; set; }
        [JsonPropertyName("n_surface")] public int NSurface { get; set; }
        [JsonPropertyName("fixed_nodes")] public int[] FixedNodes { get; set; } = System.Array.Empty<int>();
        [JsonPropertyName("surface_nodes")] public int[] SurfaceNodes { get; set; } = System.Array.Empty<int>();
        [JsonPropertyName("surface_positions")] public double[][] SurfacePositions { get; set; } = System.Array.Empty<double[]>();
        [JsonPropertyName("surface_areas")] public double[] SurfaceAreas { get; set; } = System.Array.Empty<double>();
        [JsonPropertyName("surface_triangles")] public int[][] SurfaceTriangles { get; set; } = System.Array.Empty<int[]>();
    }

    public sealed class CaseLoadEntry
    {
        [JsonPropertyName("row")] public int Row { get; set; }
        [JsonPropertyName("f")] public double[] F { get; set; } = System.Array.Empty<double>();
    }

    public sealed class CaseData
    {
        [JsonPropertyName("case_id")] public string CaseId { get; set; } = "";
        [JsonPropertyName("mesh_level")] public string MeshLevel { get; set; } = "";
        [JsonPropertyName("n_surface")] public int NSurface { get; set; }
        [JsonPropertyName("physical")] public bool Physical { get; set; }
        [JsonPropertyName("loads")] public CaseLoadEntry[] Loads { get; set; } = System.Array.Empty<CaseLoadEntry>();
    }

    public sealed class ProfileData
    {
        public string ProfileId { get; init; } = "";
        public double[] TimesS { get; init; } = System.Array.Empty<double>();
        public double[] ForcesN { get; init; } = System.Array.Empty<double>();
        public double DtForceS { get; init; } = 0.001;
        public double DurationS => TimesS.Length > 0 ? TimesS[^1] : 0.0;

        /// <summary>
        /// Evaluates piecewise-linear force amplitude (N) at arbitrary time t (s), clamped to [0, DurationS].
        /// </summary>
        public double SampleAmplitude(double timeSec)
        {
            if (TimesS.Length == 0)
                return 0.0;
            if (timeSec <= TimesS[0])
                return ForcesN[0];
            if (timeSec >= TimesS[^1])
                return ForcesN[^1];

            double exactIdx = (timeSec - TimesS[0]) / DtForceS;
            int idx = (int)System.Math.Floor(exactIdx);
            if (idx < 0) idx = 0;
            if (idx >= TimesS.Length - 1) return ForcesN[^1];

            // Adjust in case CSV step has tiny rounding drift
            while (idx + 1 < TimesS.Length && TimesS[idx + 1] < timeSec) idx++;
            while (idx > 0 && TimesS[idx] > timeSec) idx--;

            double t0 = TimesS[idx];
            double t1 = TimesS[idx + 1];
            double alpha = (t1 > t0) ? (timeSec - t0) / (t1 - t0) : 0.0;
            return ForcesN[idx] + alpha * (ForcesN[idx + 1] - ForcesN[idx]);
        }
    }

    public sealed class GMetaData
    {
        [JsonPropertyName("mesh_level")] public string MeshLevel { get; set; } = "";
        [JsonPropertyName("n_surface")] public int NSurface { get; set; }
        [JsonPropertyName("units")] public string Units { get; set; } = "";
        [JsonPropertyName("E0_Pa")] public double E0Pa { get; set; }
        [JsonPropertyName("nu")] public double Nu { get; set; }
        [JsonPropertyName("E_inf_Pa")] public double EInfPa { get; set; }
        [JsonPropertyName("E1_Pa")] public double E1Pa { get; set; }
        [JsonPropertyName("tau1_s")] public double Tau1S { get; set; }
        [JsonPropertyName("layout")] public string Layout { get; set; } = "";
    }

    public sealed class SofaPlaybackMeta
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("case_id")] public string CaseId { get; set; } = "";
        [JsonPropertyName("forces_N")] public double[] ForcesN { get; set; } = System.Array.Empty<double>();
        [JsonPropertyName("n_surface")] public int NSurface { get; set; }
    }

    public sealed class SofaPlaybackData
    {
        public SofaPlaybackMeta Meta { get; init; } = new();
        /// <summary>
        /// Flat float32 array of shape [n_frames, n_surface, 3] in contract coordinates (x,y,z) and SI metres.
        /// Frame k starts at offset k * 3 * NSurface.
        /// </summary>
        public float[] Displacements { get; init; } = System.Array.Empty<float>();
        public int NFrames => Meta.ForcesN.Length;
        public int NSurface => Meta.NSurface;
    }
}
