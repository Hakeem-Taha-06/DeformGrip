using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DeformGrip.App.Data
{
    public static class ContractLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static TestSettings LoadSettings(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Missing test_settings.json at: {path}", path);
            string json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<TestSettings>(json, JsonOptions)
                ?? throw new InvalidDataException($"Failed to parse {path}.");

            if (settings.Validity == null || settings.Validity.MaxDispFractionOfRadius <= 0.0)
                throw new InvalidDataException($"Missing or invalid 'validity.max_disp_fraction_of_radius' in {path} (CONTRACT §6.1).");

            return settings;
        }

        public static SurfaceMeshData LoadSurface(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Missing surface.json at: {path}", path);
            string json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<SurfaceMeshData>(json, JsonOptions)
                ?? throw new InvalidDataException($"Failed to parse {path}.");

            if (data.SurfacePositions.Length != data.NSurface)
                throw new InvalidDataException($"surface_positions length ({data.SurfacePositions.Length}) != n_surface ({data.NSurface}) in {path}.");
            if (data.SurfaceNodes.Length != data.NSurface)
                throw new InvalidDataException($"surface_nodes length ({data.SurfaceNodes.Length}) != n_surface ({data.NSurface}) in {path}.");

            return data;
        }

        public static CaseData LoadCase(string path, int expectedNSurface)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Missing case JSON at: {path}", path);
            string json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<CaseData>(json, JsonOptions)
                ?? throw new InvalidDataException($"Failed to parse {path}.");

            if (data.NSurface != expectedNSurface)
                throw new InvalidDataException($"Case {data.CaseId} n_surface ({data.NSurface}) != mesh n_surface ({expectedNSurface}).");

            return data;
        }

        public static ProfileData LoadProfile(string path, string profileId)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Missing profile CSV at: {path}", path);

            var times = new List<double>(8192);
            var forces = new List<double>(8192);

            using var reader = new StreamReader(path);
            string? header = reader.ReadLine();
            if (header == null || header.Trim() != "t,f_N")
                throw new InvalidDataException($"Unexpected CSV header '{header}' in {path} (expected 't,f_N').");

            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                int comma = line.IndexOf(',');
                if (comma <= 0 || comma >= line.Length - 1)
                    throw new InvalidDataException($"Malformed CSV row '{line}' in {path}.");

                double t = double.Parse(line.AsSpan(0, comma), CultureInfo.InvariantCulture);
                double f = double.Parse(line.AsSpan(comma + 1), CultureInfo.InvariantCulture);
                times.Add(t);
                forces.Add(f);
            }

            if (times.Count < 2)
                throw new InvalidDataException($"Profile {path} has fewer than 2 samples.");

            double dt = times[1] - times[0];
            return new ProfileData
            {
                ProfileId = profileId,
                TimesS = times.ToArray(),
                ForcesN = forces.ToArray(),
                DtForceS = dt
            };
        }

        public static GMetaData LoadGMeta(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Missing G.meta.json at: {path}", path);
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GMetaData>(json, JsonOptions)
                ?? throw new InvalidDataException($"Failed to parse {path}.");
        }

        public static SofaPlaybackData LoadSofaPlayback(string jsonPath, string binPath, int expectedNSurface)
        {
            if (!File.Exists(jsonPath))
                throw new FileNotFoundException($"Missing SOFA playback JSON at: {jsonPath}", jsonPath);
            if (!File.Exists(binPath))
                throw new FileNotFoundException($"Missing SOFA playback BIN at: {binPath}", binPath);

            var meta = JsonSerializer.Deserialize<SofaPlaybackMeta>(File.ReadAllText(jsonPath), JsonOptions)
                ?? throw new InvalidDataException($"Failed to parse {jsonPath}.");

            if (meta.NSurface != expectedNSurface)
                throw new InvalidDataException($"SOFA playback n_surface ({meta.NSurface}) != mesh n_surface ({expectedNSurface}).");
            if (meta.ForcesN.Length == 0)
                throw new InvalidDataException($"SOFA playback forces_N is empty in {jsonPath}.");

            int nFrames = meta.ForcesN.Length;
            long expectedFloats = (long)nFrames * meta.NSurface * 3;
            long expectedBytes = expectedFloats * sizeof(float);

            var fi = new FileInfo(binPath);
            if (fi.Length != expectedBytes)
                throw new InvalidDataException($"SOFA playback BIN size mismatch: got {fi.Length} bytes, expected {expectedBytes} bytes.");

            float[] displacements = new float[expectedFloats];
            using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            Span<byte> raw = MemoryMarshal.AsBytes(displacements.AsSpan());
            int totalRead = 0;
            while (totalRead < raw.Length)
            {
                int read = fs.Read(raw.Slice(totalRead));
                if (read == 0)
                    throw new EndOfStreamException($"Unexpected EOF reading {binPath}.");
                totalRead += read;
            }

            return new SofaPlaybackData
            {
                Meta = meta,
                Displacements = displacements
            };
        }
    }
}
