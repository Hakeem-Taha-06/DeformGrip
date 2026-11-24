using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeformGrip.App.Cli;
using DeformGrip.App.Core;
using DeformGrip.App.Data;
using DeformGrip.App.Input;
using DeformGrip.App.Simulation;
using Godot;

#if USE_CORE_STUB
using DeformGrip.App.DevStubs;
#endif

namespace DeformGrip.App.Scene
{
    public enum ViewMode
    {
        Ours,
        Sofa,
        SideBySide
    }

    /// <summary>
    /// Root controller for DeformGrip (Role 1b).
    /// - Reads all external data strictly via configurable filesystem paths (no res:// duplication).
    /// - Drives interactive Ours / SOFA / Side-by-Side modes without changing camera, case, or amplitude.
    /// - Dispatches CLI modes: --dump, --benchmark, and (when USE_CORE_STUB) --generate-stub-fixtures.
    /// </summary>
    public partial class MainController : Node3D
    {
        [ExportGroup("External Data Paths (Filesystem, Relative to godot/)")]
        [Export] public string ModelDir = "../model";
        [Export] public string ConfigDir = "../config";
        [Export] public string ExportDir = "../solver/export";
        [Export] public string SofaPlaybackDir = "../results/sofa/playback";
        [Export] public string GodotResultsDir = "../results/godot";

        private static readonly string[] LevelOrder = { "coarse", "medium", "fine" };
        private static readonly Color OursBallColor = new(0.87f, 0.48f, 0.34f); // warm coral rubber
        private static readonly Color SofaBallColor = new(0.31f, 0.66f, 0.60f); // cool teal rubber
        private static readonly Color FixedCapColor = new(0.22f, 0.25f, 0.32f); // dark slate fixed -z cap

        private ContractPaths _paths = null!;
        private TestSettings _settings = null!;

        // 3D scene elements
        private OrbitCameraController _camera = null!;
        private BallMeshView _oursBall = null!;
        private BallMeshView _sofaBall = null!;
        private Node3D _referenceGizmos = null!;
        private Label3D _oursTag3D = null!;
        private Label3D _sofaTag3D = null!;

        // Simulation state
        private string _activeLevel = "medium";
        private string _activeCaseId = "cap_medium";
        private string _activeProfileId = "slow_squeeze";
        private ViewMode _viewMode = ViewMode.Ours;
        private float _visualGain = 1.0f; // CONTRACT §3: defaults to x1, always labelled
        private double _simTimeS;
        private bool _isPlaying;
        private bool _useStubFixtures;

        private SurfaceMeshData? _surface;
        private CaseData? _activeCase;
        private ProfileData? _activeProfile;
        private CaseForceSource _forceSource = new();
        private OursRunner? _oursRunner;
        private SofaPlaybackRunner? _sofaRunner;

        private string _oursStatus = "Not loaded";
        private string _sofaStatus = "Not loaded";

        // Benchmark runner (only non-null in --benchmark CLI mode)
        private BenchmarkRunner? _benchmarkRunner;

        // UI widgets
        private Label _hudCaseLabel = null!;
        private Label _hudAmpLabel = null!;
        private Label _hudTimeLabel = null!;
        private Label _hudDispLabel = null!;
        private Label _hudGainLabel = null!;
        private Label _hudStatusLabel = null!;
        private PanelContainer _validityBanner = null!;
        private Label _validityLabel = null!;

        private OptionButton _levelOption = null!;
        private OptionButton _caseOption = null!;
        private Button _modeOursBtn = null!;
        private Button _modeSofaBtn = null!;
        private Button _modeSideBtn = null!;
        private OptionButton _inputModeOption = null!;
        private HSlider _ampSlider = null!;
        private Label _ampValueLabel = null!;
        private OptionButton _profileOption = null!;
        private Button _playPauseBtn = null!;
        private Button _resetTimeBtn = null!;
        private HSlider _gainSlider = null!;
        private Label _gainValueLabel = null!;
        private CheckButton _gizmoToggle = null!;
        private Button? _stubFixturesBtn;

        private bool _suppressUiEvents;

        public override void _Ready()
        {
            _camera = GetNode<OrbitCameraController>("OrbitCamera");

            string[] userArgs = OS.GetCmdlineUserArgs();
            if (HandleCliModes(userArgs))
                return;

            InitPaths(useStubFixtures: _useStubFixtures);
            _settings = ContractLoader.LoadSettings(_paths.SettingsJson);

            Build3DNodes();
            BuildUi();

            // Select sensible defaults from test_settings.json
            _activeLevel = _settings.MeshLevels.ContainsKey("medium") ? "medium" : _settings.MeshLevels.Keys.First();
            _activeCaseId = _settings.Cases.List.ContainsKey("cap_medium") ? "cap_medium" : _settings.Cases.List.Keys.First();
            _activeProfileId = _settings.Profiles.ContainsKey("slow_squeeze") ? "slow_squeeze" : _settings.Profiles.Keys.First();

            PopulateUiChoices();
            LoadLevel(_activeLevel);
            LoadCase(_activeCaseId);
            LoadProfile(_activeProfileId);
            ApplyViewMode(_viewMode);
            UpdateSimulationAndVisuals(0.0);
        }

        private void InitPaths(bool useStubFixtures, string? exportOverride = null, string? sofaOverride = null)
        {
            string exp = exportOverride ?? ExportDir;
            string sofa = sofaOverride ?? SofaPlaybackDir;

#if USE_CORE_STUB
            if (useStubFixtures)
            {
                string fixtures = StubFixtureGenerator.DefaultFixturesRoot(ContractPaths.DetectGodotProjectDir());
                exp = Path.Combine(fixtures, "export");
                sofa = Path.Combine(fixtures, "sofa_playback");
            }
#endif
            _paths = new ContractPaths(ModelDir, ConfigDir, exp, sofa, GodotResultsDir);
        }

        private bool HandleCliModes(string[] args)
        {
            if (args.Length == 0)
                return false;

            string? exportDirOverride = GetOptionValue(args, "--export-dir");
            string? outDirOverride = GetOptionValue(args, "--out-dir");
            bool dataStub = args.Contains("--data-stub");
            _useStubFixtures = dataStub;

#if USE_CORE_STUB
            int genIdx = Array.IndexOf(args, "--generate-stub-fixtures");
            if (genIdx >= 0)
            {
                string levelArg = (genIdx + 1 < args.Length && !args[genIdx + 1].StartsWith("--"))
                    ? args[genIdx + 1]
                    : "coarse";
                InitPaths(useStubFixtures: false);
                try
                {
                    StubFixtureGenerator.Generate(_paths, levelArg);
                    GetTree().Quit(0);
                }
                catch (Exception ex)
                {
                    GD.PushError($"[CLI] --generate-stub-fixtures failed: {ex}");
                    GetTree().Quit(1);
                }
                return true;
            }
#endif

            int dumpIdx = Array.IndexOf(args, "--dump");
            if (dumpIdx >= 0)
            {
                if (dumpIdx + 3 >= args.Length)
                {
                    GD.PushError("Usage: -- --dump <level> <case> <profile> [--export-dir <path>] [--out-dir <path>] [--data-stub]");
                    GetTree().Quit(2);
                    return true;
                }

                string level = args[dumpIdx + 1];
                string caseId = args[dumpIdx + 2];
                string profileId = args[dumpIdx + 3];
                InitPaths(useStubFixtures: dataStub, exportOverride: exportDirOverride);

                try
                {
                    int rc = DumpRunner.Run(_paths, level, caseId, profileId, outDirOverride);
                    GetTree().Quit(rc);
                }
                catch (Exception ex)
                {
                    GD.PushError($"[CLI] --dump failed: {ex}");
                    GetTree().Quit(1);
                }
                return true;
            }

            if (args.Contains("--benchmark"))
            {
                InitPaths(useStubFixtures: dataStub, exportOverride: exportDirOverride);
                _settings = ContractLoader.LoadSettings(_paths.SettingsJson);
                Build3DNodes();

                string? levelsCsv = GetOptionValue(args, "--levels");
                string[]? levelsOverride = string.IsNullOrWhiteSpace(levelsCsv)
                    ? null
                    : levelsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                int warmup = 60;
                string? warmupStr = GetOptionValue(args, "--warmup");
                if (!string.IsNullOrWhiteSpace(warmupStr) && int.TryParse(warmupStr, out int parsedWarmup))
                    warmup = parsedWarmup;

                try
                {
                    _oursBall.Visible = true;
                    _sofaBall.Visible = false;
                    _oursBall.Position = Vector3.Zero;
                    _benchmarkRunner = new BenchmarkRunner(_paths, _oursBall, levelsOverride, outDirOverride, warmup);
                }
                catch (Exception ex)
                {
                    GD.PushError($"[CLI] --benchmark init failed: {ex}");
                    GetTree().Quit(1);
                }
                return true;
            }

            return false;
        }

        private static string? GetOptionValue(string[] args, string flag)
        {
            int idx = Array.IndexOf(args, flag);
            if (idx >= 0 && idx + 1 < args.Length)
                return args[idx + 1];
            return null;
        }

        public override void _Process(double delta)
        {
            if (_benchmarkRunner != null)
            {
                _benchmarkRunner.TickFrame();
                if (_benchmarkRunner.IsFinished)
                    GetTree().Quit(_benchmarkRunner.ExitCode);
                return;
            }

            if (_forceSource.Mode == ForceInputMode.ProfileReplay && _isPlaying && _activeProfile != null)
            {
                _simTimeS += delta;
                if (_simTimeS >= _activeProfile.DurationS)
                {
                    _simTimeS = _activeProfile.DurationS;
                    _isPlaying = false;
                    _playPauseBtn.Text = "Play";
                }
                UpdateSimulationAndVisuals(delta);
            }
        }

        private void Build3DNodes()
        {
            _oursBall = new BallMeshView { Name = "OursBall" };
            _sofaBall = new BallMeshView { Name = "SofaBall" };
            AddChild(_oursBall);
            AddChild(_sofaBall);

            float r = (float)(_settings?.Geometry.RadiusM ?? 0.035);

            _oursTag3D = new Label3D
            {
                Text = "OURS (FEM G)",
                PixelSize = 0.00035f,
                Position = new Vector3(0, r * 1.42f, 0),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = new Color(1.0f, 0.85f, 0.78f),
                Visible = false
            };
            _oursBall.AddChild(_oursTag3D);

            _sofaTag3D = new Label3D
            {
                Text = "SOFA (Reference)",
                PixelSize = 0.00035f,
                Position = new Vector3(0, r * 1.42f, 0),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = new Color(0.76f, 0.96f, 0.92f),
                Visible = false
            };
            _sofaBall.AddChild(_sofaTag3D);

            // Subtle reference pedestal & contract-axis triad for Open Item O5 visual verification
            _referenceGizmos = new Node3D { Name = "ReferenceGizmos" };
            AddChild(_referenceGizmos);

            var pedestalMesh = new CylinderMesh
            {
                TopRadius = r * 0.65f,
                BottomRadius = r * 0.72f,
                Height = 0.002f,
                RadialSegments = 36
            };
            var pedestal = new MeshInstance3D
            {
                Mesh = pedestalMesh,
                Position = new Vector3(0, -r - 0.0012f, 0),
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.18f, 0.20f, 0.24f),
                    Roughness = 0.85f
                }
            };
            _referenceGizmos.AddChild(pedestal);

            // Contract coordinate triad offset slightly to the side so polar +z (vertical up) is unmistakable
            Vector3 triadOrigin = new(-r * 2.15f, -r, 0);
            float axisLen = r * 0.75f;
            AddAxisArrow(_referenceGizmos, triadOrigin, new Vector3(axisLen, 0, 0), new Color(0.95f, 0.32f, 0.32f), "+X");
            AddAxisArrow(_referenceGizmos, triadOrigin, new Vector3(0, axisLen, 0), new Color(0.32f, 0.62f, 1.0f), "+Z (polar up)");
            AddAxisArrow(_referenceGizmos, triadOrigin, new Vector3(0, 0, -axisLen), new Color(0.36f, 0.88f, 0.45f), "+Y");
        }

        private static void AddAxisArrow(Node3D parent, Vector3 origin, Vector3 vec, Color color, string label)
        {
            var im = new ImmediateMesh();
            im.SurfaceBegin(Mesh.PrimitiveType.Lines);
            im.SurfaceAddVertex(origin);
            im.SurfaceAddVertex(origin + vec);
            im.SurfaceEnd();

            var mi = new MeshInstance3D
            {
                Mesh = im,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = color
                }
            };
            parent.AddChild(mi);

            var lbl = new Label3D
            {
                Text = label,
                PixelSize = 0.00024f,
                Position = origin + vec * 1.15f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = color
            };
            parent.AddChild(lbl);
        }

        private void BuildUi()
        {
            var layer = new CanvasLayer { Name = "HudLayer" };
            AddChild(layer);

            // --- Top-Left Always-Visible HUD ---
            var hudPanel = new PanelContainer
            {
                Position = new Vector2(14, 14),
                CustomMinimumSize = new Vector2(360, 0)
            };
            hudPanel.AddThemeStyleboxOverride("panel", MakePanelStyle(new Color(0.08f, 0.10f, 0.14f, 0.84f)));
            layer.AddChild(hudPanel);

            var hudVbox = new VBoxContainer();
            hudVbox.AddThemeConstantOverride("separation", 4);
            hudPanel.AddChild(hudVbox);

            var title = new Label { Text = "DeformGrip — Live Telemetry" };
            title.AddThemeFontSizeOverride("font_size", 16);
            title.AddThemeColorOverride("font_color", new Color(0.95f, 0.97f, 1.0f));
            hudVbox.AddChild(title);

            _hudCaseLabel = AddHudLine(hudVbox);
            _hudAmpLabel = AddHudLine(hudVbox);
            _hudTimeLabel = AddHudLine(hudVbox);
            _hudDispLabel = AddHudLine(hudVbox);
            _hudGainLabel = AddHudLine(hudVbox);
            _hudStatusLabel = AddHudLine(hudVbox, new Color(0.75f, 0.82f, 0.90f), 12);

            // High-contrast small-strain validity warning banner (CONTRACT §4.6, godot/README.md Task 6)
            _validityBanner = new PanelContainer { Visible = false };
            var warnStyle = MakePanelStyle(new Color(0.58f, 0.12f, 0.10f, 0.95f));
            warnStyle.BorderColor = new Color(1.0f, 0.72f, 0.25f, 1.0f);
            warnStyle.BorderWidthLeft = 2;
            warnStyle.BorderWidthRight = 2;
            warnStyle.BorderWidthTop = 2;
            warnStyle.BorderWidthBottom = 2;
            _validityBanner.AddThemeStyleboxOverride("panel", warnStyle);
            _validityLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            _validityLabel.AddThemeFontSizeOverride("font_size", 13);
            _validityLabel.AddThemeColorOverride("font_color", new Color(1.0f, 0.94f, 0.82f));
            _validityBanner.AddChild(_validityLabel);
            hudVbox.AddChild(_validityBanner);

            // --- Top-Right Controls Panel ---
            var ctrlPanel = new PanelContainer
            {
                AnchorLeft = 1f,
                AnchorRight = 1f,
                AnchorTop = 0f,
                AnchorBottom = 0f,
                OffsetLeft = -334,
                OffsetRight = -14,
                OffsetTop = 14,
                OffsetBottom = 560
            };
            ctrlPanel.AddThemeStyleboxOverride("panel", MakePanelStyle(new Color(0.08f, 0.10f, 0.14f, 0.86f)));
            layer.AddChild(ctrlPanel);

            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            ctrlPanel.AddChild(scroll);

            var vbox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            vbox.AddThemeConstantOverride("separation", 8);
            scroll.AddChild(vbox);

            var ctrlTitle = new Label { Text = "Controls" };
            ctrlTitle.AddThemeFontSizeOverride("font_size", 16);
            vbox.AddChild(ctrlTitle);

            // Mesh Level
            vbox.AddChild(new Label { Text = "Mesh Level" });
            _levelOption = new OptionButton();
            _levelOption.ItemSelected += idx =>
            {
                if (_suppressUiEvents) return;
                string lvl = _levelOption.GetItemText((int)idx);
                LoadLevel(lvl);
                LoadCase(_activeCaseId);
                UpdateSimulationAndVisuals(0.0);
            };
            vbox.AddChild(_levelOption);

            // Case
            vbox.AddChild(new Label { Text = "Load Case" });
            _caseOption = new OptionButton();
            _caseOption.ItemSelected += idx =>
            {
                if (_suppressUiEvents) return;
                string cid = _caseOption.GetItemMetadata((int)idx).AsString();
                LoadCase(cid);
                UpdateSimulationAndVisuals(0.0);
            };
            vbox.AddChild(_caseOption);

            // View Mode toggle (never changes camera, case, or amplitude)
            vbox.AddChild(new Label { Text = "View Mode" });
            var modeRow = new HBoxContainer();
            modeRow.AddThemeConstantOverride("separation", 6);
            vbox.AddChild(modeRow);

            _modeOursBtn = new Button { Text = "Ours", ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _modeSofaBtn = new Button { Text = "SOFA", ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _modeSideBtn = new Button { Text = "Side-by-Side", ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _modeOursBtn.Pressed += () => ApplyViewMode(ViewMode.Ours);
            _modeSofaBtn.Pressed += () => ApplyViewMode(ViewMode.Sofa);
            _modeSideBtn.Pressed += () => ApplyViewMode(ViewMode.SideBySide);
            modeRow.AddChild(_modeOursBtn);
            modeRow.AddChild(_modeSofaBtn);
            modeRow.AddChild(_modeSideBtn);

            // Input Source
            vbox.AddChild(new Label { Text = "Force Input" });
            _inputModeOption = new OptionButton();
            _inputModeOption.AddItem("Manual Slider (0–20 N)", 0);
            _inputModeOption.AddItem("Profile CSV Replay", 1);
            _inputModeOption.ItemSelected += idx =>
            {
                if (_suppressUiEvents) return;
                _forceSource.Mode = idx == 0 ? ForceInputMode.ManualSlider : ForceInputMode.ProfileReplay;
                _isPlaying = false;
                _playPauseBtn.Text = "Play";
                RefreshInputWidgetsEnabled();
                UpdateSimulationAndVisuals(0.0);
            };
            vbox.AddChild(_inputModeOption);

            // Manual Slider
            _ampValueLabel = new Label { Text = "Amplitude: 0.0 N" };
            vbox.AddChild(_ampValueLabel);
            _ampSlider = new HSlider { MinValue = 0.0, MaxValue = 20.0, Step = 0.1, Value = 0.0 };
            _ampSlider.ValueChanged += val =>
            {
                if (_suppressUiEvents) return;
                _forceSource.ManualAmplitudeN = val;
                _ampValueLabel.Text = $"Amplitude: {val:F1} N";
                UpdateSimulationAndVisuals(0.0);
            };
            vbox.AddChild(_ampSlider);

            // Profile selector + playback buttons
            _profileOption = new OptionButton();
            _profileOption.ItemSelected += idx =>
            {
                if (_suppressUiEvents) return;
                LoadProfile(_profileOption.GetItemText((int)idx));
                UpdateSimulationAndVisuals(0.0);
            };
            vbox.AddChild(_profileOption);

            var playRow = new HBoxContainer();
            playRow.AddThemeConstantOverride("separation", 6);
            vbox.AddChild(playRow);

            _playPauseBtn = new Button { Text = "Play", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _playPauseBtn.Pressed += () =>
            {
                if (_forceSource.Mode != ForceInputMode.ProfileReplay)
                {
                    _forceSource.Mode = ForceInputMode.ProfileReplay;
                    _suppressUiEvents = true;
                    _inputModeOption.Selected = 1;
                    _suppressUiEvents = false;
                    RefreshInputWidgetsEnabled();
                }
                if (_activeProfile != null && _simTimeS >= _activeProfile.DurationS - 1e-6)
                {
                    _simTimeS = 0.0;
                    _oursRunner?.Reset();
                }
                _isPlaying = !_isPlaying;
                _playPauseBtn.Text = _isPlaying ? "Pause" : "Play";
            };
            _resetTimeBtn = new Button { Text = "Reset", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _resetTimeBtn.Pressed += () =>
            {
                _isPlaying = false;
                _playPauseBtn.Text = "Play";
                _simTimeS = 0.0;
                _oursRunner?.Reset();
                UpdateSimulationAndVisuals(0.0);
            };
            playRow.AddChild(_playPauseBtn);
            playRow.AddChild(_resetTimeBtn);

            // Visual Gain (default x1, up to x50, always labelled)
            _gainValueLabel = new Label { Text = "Visual Gain: x1 (true scale)" };
            vbox.AddChild(_gainValueLabel);
            _gainSlider = new HSlider { MinValue = 1.0, MaxValue = 50.0, Step = 1.0, Value = 1.0 };
            _gainSlider.ValueChanged += val =>
            {
                if (_suppressUiEvents) return;
                _visualGain = (float)val;
                _gainValueLabel.Text = _visualGain <= 1.001f
                    ? "Visual Gain: x1 (true scale)"
                    : $"Visual Gain: x{_visualGain:F0} (visual only)";
                UpdateSimulationAndVisuals(0.0);
            };
            vbox.AddChild(_gainSlider);

            // Reference gizmos & Camera Reset
            _gizmoToggle = new CheckButton { Text = "Show Pedestal & +Z Axis", ButtonPressed = true };
            _gizmoToggle.Toggled += on => _referenceGizmos.Visible = on;
            vbox.AddChild(_gizmoToggle);

            var resetCamBtn = new Button { Text = "Reset Camera (R)" };
            resetCamBtn.Pressed += () => _camera.ResetView();
            vbox.AddChild(resetCamBtn);

#if USE_CORE_STUB
            string fixturesDir = StubFixtureGenerator.DefaultFixturesRoot(_paths.BaseDir);
            _stubFixturesBtn = new Button
            {
                Text = _useStubFixtures ? "Using _dev_stubs/fixtures (Switch to Real)" : "Switch to _dev_stubs/fixtures",
                TooltipText = $"Toggle local synthetic fixtures in {fixturesDir} while Role 2b/3 binaries are pending."
            };
            _stubFixturesBtn.Pressed += () =>
            {
                _useStubFixtures = !_useStubFixtures;
                InitPaths(useStubFixtures: _useStubFixtures);
                _stubFixturesBtn.Text = _useStubFixtures
                    ? "Using _dev_stubs/fixtures (Switch to Real)"
                    : "Switch to _dev_stubs/fixtures";
                LoadLevel(_activeLevel);
                LoadCase(_activeCaseId);
                UpdateSimulationAndVisuals(0.0);
            };
            vbox.AddChild(_stubFixturesBtn);
#endif
        }

        private static StyleBoxFlat MakePanelStyle(Color bg)
        {
            return new StyleBoxFlat
            {
                BgColor = bg,
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8,
                ContentMarginLeft = 12,
                ContentMarginRight = 12,
                ContentMarginTop = 10,
                ContentMarginBottom = 10
            };
        }

        private static Label AddHudLine(VBoxContainer parent, Color? color = null, int fontSize = 14)
        {
            var lbl = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
            lbl.AddThemeFontSizeOverride("font_size", fontSize);
            if (color.HasValue)
                lbl.AddThemeColorOverride("font_color", color.Value);
            parent.AddChild(lbl);
            return lbl;
        }

        private void PopulateUiChoices()
        {
            _suppressUiEvents = true;

            _levelOption.Clear();
            int selLevel = 0;
            int idx = 0;
            foreach (string lvl in LevelOrder)
            {
                if (!_settings.MeshLevels.ContainsKey(lvl)) continue;
                _levelOption.AddItem(lvl, idx);
                if (lvl == _activeLevel) selLevel = idx;
                idx++;
            }
            _levelOption.Selected = selLevel;

            _caseOption.Clear();
            int selCase = 0;
            idx = 0;
            foreach (string cid in _settings.Cases.List.Keys)
            {
                bool isNonPhys = cid.StartsWith("random_", StringComparison.OrdinalIgnoreCase);
                string label = isNonPhys ? $"{cid} [non-physical]" : cid;
                _caseOption.AddItem(label, idx);
                _caseOption.SetItemMetadata(idx, cid);
                if (cid == _activeCaseId) selCase = idx;
                idx++;
            }
            _caseOption.Selected = selCase;

            _profileOption.Clear();
            int selProf = 0;
            idx = 0;
            foreach (string pid in _settings.Profiles.Keys)
            {
                _profileOption.AddItem(pid, idx);
                if (pid == _activeProfileId) selProf = idx;
                idx++;
            }
            _profileOption.Selected = selProf;

            _suppressUiEvents = false;
            RefreshInputWidgetsEnabled();
        }

        private void RefreshInputWidgetsEnabled()
        {
            bool manual = _forceSource.Mode == ForceInputMode.ManualSlider;
            _ampSlider.Editable = manual;
            _profileOption.Disabled = manual;
            _playPauseBtn.Disabled = manual;
        }

        private void LoadLevel(string level)
        {
            _activeLevel = level;
            _surface = ContractLoader.LoadSurface(_paths.SurfaceJson(level));
            _oursBall.InitializeMesh(_surface, OursBallColor, FixedCapColor);
            _sofaBall.InitializeMesh(_surface, SofaBallColor, FixedCapColor);
            _sofaRunner = new SofaPlaybackRunner(_surface.NSurface);

            string gBinPath = _paths.GBin(level);
            if (File.Exists(gBinPath))
            {
                try
                {
                    IDeformCore core = DeformCoreFactory.Load(
                        gBinPath,
                        _surface.NSurface,
                        _settings.Stage2.Enabled,
                        _settings.Stage2.Model,
                        _settings.Material.EInfPa,
                        _settings.Material.E1Pa,
                        _settings.Material.Tau1S);
                    _oursRunner = new OursRunner(core, _settings.Timing.DtForceS);
                    _oursStatus = core.IsStub
                        ? (_useStubFixtures ? "STUB core + synthetic G.bin" : "STUB core + G.bin")
                        : "REAL core + G.bin";
                }
                catch (Exception ex)
                {
                    _oursRunner = null;
                    _oursStatus = $"G.bin load error: {ex.Message}";
                    GD.PushWarning($"[MainController] {_oursStatus}");
                }
            }
            else
            {
                _oursRunner = null;
                _oursStatus = $"Missing {Path.GetFileName(gBinPath)} (rest mesh only)";
            }

            GD.Print($"[MainController] Loaded level '{level}': n_surface={_surface.NSurface}, update={_oursBall.ActiveUpdateMethod}, ours={_oursStatus}");
        }

        private void LoadCase(string caseId)
        {
            _activeCaseId = caseId;
            if (_surface == null)
                return;

            _activeCase = ContractLoader.LoadCase(_paths.CaseJson(_activeLevel, caseId), _surface.NSurface);
            _forceSource.SetCase(_activeCase);
            _oursRunner?.Reset();

            // Load SOFA playback table only for physical cases (CONTRACT §6.7, godot/README.md Task 5)
            if (!_activeCase.Physical)
            {
                _sofaRunner?.SetPlaybackData(null, _surface.NSurface);
                _sofaStatus = "Disabled (non-physical case)";
                if (_viewMode != ViewMode.Ours)
                    ApplyViewMode(ViewMode.Ours);
            }
            else
            {
                string jsonPath = _paths.SofaForceTableJson(_activeLevel, caseId);
                string binPath = _paths.SofaForceTableBin(_activeLevel, caseId);
                if (File.Exists(jsonPath) && File.Exists(binPath))
                {
                    try
                    {
                        SofaPlaybackData pb = ContractLoader.LoadSofaPlayback(jsonPath, binPath, _surface.NSurface);
                        _sofaRunner?.SetPlaybackData(pb, _surface.NSurface);
                        _sofaStatus = _useStubFixtures ? "Synthetic SOFA table" : "SOFA table loaded";
                    }
                    catch (Exception ex)
                    {
                        _sofaRunner?.SetPlaybackData(null, _surface.NSurface);
                        _sofaStatus = $"SOFA load error: {ex.Message}";
                    }
                }
                else
                {
                    _sofaRunner?.SetPlaybackData(null, _surface.NSurface);
                    _sofaStatus = "Missing SOFA playback file";
                }
            }

            RefreshModeButtons();
        }

        private void LoadProfile(string profileId)
        {
            _activeProfileId = profileId;
            _activeProfile = ContractLoader.LoadProfile(_paths.ProfileCsv(profileId), profileId);
            _forceSource.SetProfile(_activeProfile);
            _simTimeS = 0.0;
            _isPlaying = false;
            _playPauseBtn.Text = "Play";
            _oursRunner?.Reset();
        }

        private void RefreshModeButtons()
        {
            bool physical = _activeCase?.Physical ?? false;
            bool sofaReady = physical && (_sofaRunner?.IsLoaded ?? false);

            _modeSofaBtn.Disabled = !physical;
            _modeSideBtn.Disabled = !physical;

            string disabledTip = !physical
                ? "Disabled: non-physical random cases have no SOFA playback table (CONTRACT §6.1 & §6.7)."
                : (!sofaReady ? "SOFA playback file not found yet; shows rest mesh until Role 3 delivers playback." : "");

            _modeSofaBtn.TooltipText = disabledTip;
            _modeSideBtn.TooltipText = disabledTip;

            _modeOursBtn.ButtonPressed = _viewMode == ViewMode.Ours;
            _modeSofaBtn.ButtonPressed = _viewMode == ViewMode.Sofa;
            _modeSideBtn.ButtonPressed = _viewMode == ViewMode.SideBySide;
        }

        private void ApplyViewMode(ViewMode mode)
        {
            // Enforce non-physical guard
            if (_activeCase != null && !_activeCase.Physical && mode != ViewMode.Ours)
                mode = ViewMode.Ours;

            _viewMode = mode;

            // IMPORTANT (godot/README.md acceptance check): switching modes MUST NOT modify the camera,
            // selected case, or amplitude. Only ball visibility and local X offsets change.
            float r = (float)(_surface?.RadiusM ?? _settings.Geometry.RadiusM);
            float sideOffset = r * 1.38f;

            switch (_viewMode)
            {
                case ViewMode.Ours:
                    _oursBall.Visible = true;
                    _sofaBall.Visible = false;
                    _oursBall.Position = Vector3.Zero;
                    _oursTag3D.Visible = false;
                    _sofaTag3D.Visible = false;
                    break;

                case ViewMode.Sofa:
                    _oursBall.Visible = false;
                    _sofaBall.Visible = true;
                    _sofaBall.Position = Vector3.Zero;
                    _oursTag3D.Visible = false;
                    _sofaTag3D.Visible = false;
                    break;

                case ViewMode.SideBySide:
                    _oursBall.Visible = true;
                    _sofaBall.Visible = true;
                    _oursBall.Position = new Vector3(-sideOffset, 0, 0); // Left = Ours
                    _sofaBall.Position = new Vector3(sideOffset, 0, 0);  // Right = SOFA
                    _oursTag3D.Visible = true;
                    _sofaTag3D.Visible = true;
                    break;
            }

            RefreshModeButtons();
            UpdateSimulationAndVisuals(0.0);
        }

        private void UpdateSimulationAndVisuals(double _)
        {
            double ampN = _forceSource.EvaluateAmplitude(_simTimeS);

            // Ours update
            if (_oursRunner != null)
            {
                if (_forceSource.Mode == ForceInputMode.ManualSlider && !_oursRunner.Stage2Enabled)
                    _oursRunner.EvaluateCaseAtAmplitude(_forceSource, ampN);
                else
                    _oursRunner.StepFrame(_simTimeS, _forceSource);

                _oursBall.ApplyDisplacements(_oursRunner.Displacements, _visualGain);
            }
            else
            {
                _oursBall.ResetToRest();
            }

            // SOFA update
            if (_sofaRunner != null && _sofaRunner.IsLoaded)
            {
                _sofaRunner.EvaluateAtAmplitude(ampN);
                _sofaBall.ApplyDisplacements(_sofaRunner.Displacements, _visualGain);
            }
            else
            {
                _sofaBall.ResetToRest();
            }

            RefreshHud(ampN);
        }

        private void RefreshHud(double ampN)
        {
            string physTag = (_activeCase?.Physical ?? true) ? "physical" : "non-physical";
            _hudCaseLabel.Text = $"Case: {_activeCaseId} ({physTag})  |  Level: {_activeLevel}";
            _hudAmpLabel.Text = $"Amplitude: {ampN:F2} N  ({_forceSource.SourceLabel})";
            _hudTimeLabel.Text = _forceSource.Mode == ForceInputMode.ProfileReplay && _activeProfile != null
                ? $"Time: {_simTimeS:F3} / {_activeProfile.DurationS:F2} s"
                : $"Time: {_simTimeS:F3} s (static)";

            float oursMax = _oursRunner?.MaxDisplacementM ?? 0f;
            float sofaMax = (_sofaRunner != null && _sofaRunner.IsLoaded) ? _sofaRunner.MaxDisplacementM : 0f;

            _hudDispLabel.Text = _viewMode switch
            {
                ViewMode.Ours => $"Largest disp (Ours): {oursMax * 1000f:F4} mm ({oursMax:E3} m)",
                ViewMode.Sofa => $"Largest disp (SOFA): {sofaMax * 1000f:F4} mm ({sofaMax:E3} m)",
                ViewMode.SideBySide => $"Largest disp — Ours: {oursMax * 1000f:F4} mm | SOFA: {sofaMax * 1000f:F4} mm",
                _ => ""
            };

            _hudGainLabel.Text = _visualGain <= 1.001f
                ? "Visual Gain: x1 (true scale, SI)"
                : $"Visual Gain: x{_visualGain:F0}  [EXAGGERATED — DISPLAY ONLY]";
            _hudGainLabel.AddThemeColorOverride(
                "font_color",
                _visualGain <= 1.001f ? new Color(0.86f, 0.94f, 0.88f) : new Color(1.0f, 0.80f, 0.36f));

            string stage2Tag = _settings.Stage2.Enabled ? $"Stage 2 ({_settings.Stage2.Model})" : "Stage 1 (elastic)";
            _hudStatusLabel.Text =
                $"{stage2Tag}  |  Ours: {_oursStatus}\n" +
                $"SOFA: {_sofaStatus}\n" +
                $"VBO: {_oursBall.ActiveUpdateMethod}";

            // Validity limit check (CONTRACT §4.6 & §6.1, godot/README.md Task 6):
            // Compare true physical SI max displacement (never scaled by visual gain) against
            // validity.max_disp_fraction_of_radius * radius_m read from test_settings.json.
            double radiusM = _surface?.RadiusM ?? _settings.Geometry.RadiusM;
            double maxFrac = _settings.Validity!.MaxDispFractionOfRadius;
            double limitM = maxFrac * radiusM;

            float displayedMaxM = _viewMode switch
            {
                ViewMode.Ours => oursMax,
                ViewMode.Sofa => sofaMax,
                ViewMode.SideBySide => MathF.Max(oursMax, sofaMax),
                _ => oursMax
            };

            bool beyondValidity = displayedMaxM > limitM;
            _validityBanner.Visible = beyondValidity;
            if (beyondValidity)
            {
                double pctOfR = radiusM > 0.0 ? (displayedMaxM / radiusM) * 100.0 : 0.0;
                _validityLabel.Text =
                    $"WARNING: Linear model outside validity range!\n" +
                    $"Max disp {displayedMaxM * 1000f:F3} mm ({pctOfR:F1}% of R) > " +
                    $"limit {limitM * 1000.0:F2} mm ({maxFrac * 100.0:F0}% of R)";
            }
        }
    }
}
