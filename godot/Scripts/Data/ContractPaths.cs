using System;
using System.IO;
using Godot;

namespace DeformGrip.App.Data
{
    /// <summary>
    /// Resolves external data paths on the host filesystem (never duplicating data under res://).
    /// Handles both running from the Godot project folder (godot/) and running from an exported
    /// binary placed in a subdirectory such as godot/build/.
    /// </summary>
    public sealed class ContractPaths
    {
        public string BaseDir { get; }
        public string ModelDir { get; }
        public string ConfigDir { get; }
        public string ExportDir { get; }
        public string SofaPlaybackDir { get; }
        public string GodotResultsDir { get; }

        public ContractPaths(
            string modelDir,
            string configDir,
            string exportDir,
            string sofaPlaybackDir,
            string godotResultsDir)
        {
            BaseDir = DetectGodotProjectDir();
            ModelDir = ResolvePath(BaseDir, modelDir);
            ConfigDir = ResolvePath(BaseDir, configDir);
            ExportDir = ResolvePath(BaseDir, exportDir);
            SofaPlaybackDir = ResolvePath(BaseDir, sofaPlaybackDir);
            GodotResultsDir = ResolvePath(BaseDir, godotResultsDir);
        }

        public string SettingsJson => Path.Combine(ConfigDir, "test_settings.json");
        public string SurfaceJson(string level) => Path.Combine(ModelDir, level, "surface.json");
        public string CaseJson(string level, string caseId) => Path.Combine(ConfigDir, "cases", level, $"{caseId}.json");
        public string ProfileCsv(string profileId) => Path.Combine(ConfigDir, "profiles", $"{profileId}.csv");
        public string GBin(string level) => Path.Combine(ExportDir, level, "G.bin");
        public string GMeta(string level) => Path.Combine(ExportDir, level, "G.meta.json");
        public string SofaForceTableJson(string level, string caseId) => Path.Combine(SofaPlaybackDir, $"{level}_{caseId}_forcetable.json");
        public string SofaForceTableBin(string level, string caseId) => Path.Combine(SofaPlaybackDir, $"{level}_{caseId}_forcetable.bin");
        public string DumpBin(string level, string caseId, string profileId) => Path.Combine(GodotResultsDir, "dumps", $"ours_{level}_{caseId}_{profileId}.bin");
        public string TimingJson(string level, string caseId) => Path.Combine(GodotResultsDir, "timing", $"{level}_{caseId}.json");
        public string VersionTxt => Path.Combine(BaseDir, "VERSION.txt");

        public static string DetectGodotProjectDir()
        {
            // 1. In editor or `godot --path godot`, ProjectSettings.GlobalizePath("res://") points directly to godot/.
            string resGlobal = ProjectSettings.GlobalizePath("res://").TrimEnd('/', '\\');
            if (!string.IsNullOrEmpty(resGlobal) && Directory.Exists(resGlobal) &&
                Directory.Exists(Path.Combine(resGlobal, "..", "config")))
            {
                return Path.GetFullPath(resGlobal);
            }

            // 2. In an exported build (e.g. godot/build/DeformGrip.x86_64), search upwards from executable dir.
            string exeDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? Directory.GetCurrentDirectory();
            string? dir = Path.GetFullPath(exeDir);
            for (int depth = 0; depth < 6 && dir != null; depth++)
            {
                if (Directory.Exists(Path.Combine(dir, "config")) && Directory.Exists(Path.Combine(dir, "model")))
                {
                    string godotSub = Path.Combine(dir, "godot");
                    return Directory.Exists(godotSub) ? godotSub : dir;
                }
                dir = Directory.GetParent(dir)?.FullName;
            }

            // 3. Fallback: current working directory
            string cwd = Directory.GetCurrentDirectory();
            if (Directory.Exists(Path.Combine(cwd, "godot")) && Directory.Exists(Path.Combine(cwd, "config")))
                return Path.GetFullPath(Path.Combine(cwd, "godot"));

            return Path.GetFullPath(cwd);
        }

        public static string ResolvePath(string baseDir, string configuredPath)
        {
            if (Path.IsPathRooted(configuredPath))
                return Path.GetFullPath(configuredPath);
            return Path.GetFullPath(Path.Combine(baseDir, configuredPath));
        }

        public string ReadPinnedVersion()
        {
            if (File.Exists(VersionTxt))
                return File.ReadAllText(VersionTxt).Trim();
            var info = Engine.GetVersionInfo();
            return $"v{info["string"]}";
        }
    }
}
