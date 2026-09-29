using System.ComponentModel;
using System.IO;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;

namespace Lasero.Tests;

/// <summary>
/// The wording and state logic that lets a first-time owner get through the app without a manual:
/// contextual hints, the machine next step, the Start summary and actionable errors. These are
/// view-model-level checks; the XAML only displays what these decide.
/// </summary>
public sealed class FirstRunGuidanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-firstrun-tests", Guid.NewGuid().ToString("N"));

    public FirstRunGuidanceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static DesignerHintContext Context(
        DesignerTool tool = DesignerTool.Select,
        int objects = 1,
        int selected = 0,
        bool vectorPath = false,
        bool nodeEdit = false,
        int nodes = 0) =>
        new(tool, objects, selected, vectorPath, nodeEdit, nodes);

    // ------------------------------------------------------------------ canvas hints

    [Fact]
    public void EmptyCanvasHasNoHintBecauseItShowsItsOwnEmptyState()
    {
        Assert.Null(GuidanceText.DesignerHint(Context(objects: 0)));
    }

    [Fact]
    public void NothingSelectedAsksForAnObject()
    {
        var hint = GuidanceText.DesignerHint(Context(objects: 3, selected: 0));

        Assert.NotNull(hint);
        Assert.Equal("Vyberte objekt, jehož vlastnosti chcete upravit", hint.Text);
        Assert.Equal("no-selection", hint.Key);
    }

    [Fact]
    public void ASelectedShapeNeedsNoHint()
    {
        Assert.Null(GuidanceText.DesignerHint(Context(selected: 1)));
    }

    [Fact]
    public void SelectedVectorPathTellsHowToReachItsNodes()
    {
        var hint = GuidanceText.DesignerHint(Context(selected: 1, vectorPath: true));

        Assert.NotNull(hint);
        Assert.Equal("path-selected", hint.Key);
        Assert.Contains("Dvojklikem", hint.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "nodes-none")]
    [InlineData(1, "nodes-one")]
    [InlineData(2, "nodes-many")]
    [InlineData(5, "nodes-many")]
    public void NodeEditHintFollowsHowManyNodesAreSelected(int nodes, string key)
    {
        var hint = GuidanceText.DesignerHint(Context(selected: 1, vectorPath: true, nodeEdit: true, nodes: nodes));

        Assert.NotNull(hint);
        Assert.Equal(key, hint.Key);
    }

    [Fact]
    public void SeveralSelectedNodesMentionTheRightClick()
    {
        var hint = GuidanceText.DesignerHint(Context(nodeEdit: true, nodes: 2));

        Assert.Contains("pravým tlačítkem", hint!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeEditOutranksEveryOtherHint()
    {
        var hint = GuidanceText.DesignerHint(Context(tool: DesignerTool.Text, objects: 0, nodeEdit: true, nodes: 0));

        Assert.Equal("nodes-none", hint!.Key);
    }

    [Theory]
    [InlineData(DesignerTool.Text, "tool-text")]
    [InlineData(DesignerTool.Line, "tool-line")]
    [InlineData(DesignerTool.Rectangle, "tool-shape")]
    [InlineData(DesignerTool.Ellipse, "tool-shape")]
    [InlineData(DesignerTool.Star, "tool-shape")]
    public void ArmedToolExplainsWhatAClickDoes(DesignerTool tool, string key)
    {
        var hint = GuidanceText.DesignerHint(Context(tool: tool, objects: 0));

        Assert.NotNull(hint);
        Assert.Equal(key, hint.Key);
    }

    [Fact]
    public void PanToolNeedsNoHint()
    {
        Assert.Null(GuidanceText.DesignerHint(Context(tool: DesignerTool.Pan)));
    }

    [Fact]
    public void EveryHintKeyIsUniquePerWording()
    {
        var all = new List<CanvasHint>();
        foreach (var tool in Enum.GetValues<DesignerTool>())
            foreach (var objects in new[] { 0, 2 })
                foreach (var selected in new[] { 0, 1 })
                    foreach (var vector in new[] { false, true })
                        foreach (var nodes in new[] { -1, 0, 1, 2 })
                        {
                            var hint = GuidanceText.DesignerHint(Context(tool, objects, selected, vector, nodes >= 0, Math.Max(nodes, 0)));
                            if (hint is not null) all.Add(hint);
                        }

        // The same key must always carry the same text, otherwise a dismissal would hide a different sentence.
        foreach (var group in all.GroupBy(hint => hint.Key))
            Assert.Single(group.Select(hint => hint.Text).Distinct());
    }

    // ------------------------------------------------------------------ machine next step

    [Fact]
    public void ConnectALaserIsTheNextStepOnlyOnceThereIsADesignAndNoMachine()
    {
        Assert.Equal(GuidanceText.NoMachine, GuidanceText.MachineNextStep(hasDesign: true, isConnected: false, isConnecting: false));
        Assert.Null(GuidanceText.MachineNextStep(hasDesign: false, isConnected: false, isConnecting: false));
        Assert.Null(GuidanceText.MachineNextStep(hasDesign: true, isConnected: true, isConnecting: false));
        Assert.Null(GuidanceText.MachineNextStep(hasDesign: true, isConnected: false, isConnecting: true));
    }

    [Fact]
    public void MachineHintAndPreflightReasonAreTheSameSentence()
    {
        var result = JobPreflight.Evaluate(new JobPreflightContext
        {
            IsConnected = false,
            Document = null,
            MachineStatus = null,
            RequireFraming = false,
            HasFramedCurrentDocument = false,
            WorkAreaWidthMm = 400,
            WorkAreaHeightMm = 400,
        });

        var disconnected = Assert.Single(result.Issues, issue => issue.Code == "device.disconnected");
        Assert.Equal(GuidanceText.NoMachine, disconnected.Message);
        Assert.Contains("Připojte laser", GuidanceText.NoMachine, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusStripSuggestsConnectingOnceTheCanvasHoldsADesign()
    {
        using var machine = new GrblConnection(new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero });
        var scene = new SceneViewModel();
        var gcode = new GCodeViewModel(machine, scene, new AppSettingsStore(Path.Combine(_directory, "strip.json")));
        var changed = new List<string?>();
        ((INotifyPropertyChanged)gcode).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Null(gcode.StatusStripMessage);

        scene.Execute(new AddObjectCommand(scene.Scene, new SceneObject
        {
            LocalShapes = [],
            LocalPivot = Position.Zero,
            LocalBounds = BoundingBox2D.Empty,
            Name = "Objekt",
        }, []));

        Assert.Contains(nameof(GCodeViewModel.StatusStripMessage), changed);
        Assert.Equal(GuidanceText.NoMachine, gcode.StatusStripMessage);
    }

    [Fact]
    public void AnErrorMessageAlwaysOutranksTheConnectSuggestion()
    {
        using var machine = new GrblConnection(new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero });
        var scene = new SceneViewModel();
        var gcode = new GCodeViewModel(machine, scene, new AppSettingsStore(Path.Combine(_directory, "strip2.json")));
        scene.Execute(new AddObjectCommand(scene.Scene, new SceneObject
        {
            LocalShapes = [],
            LocalPivot = Position.Zero,
            LocalBounds = BoundingBox2D.Empty,
            Name = "Objekt",
        }, []));

        gcode.LastMessage = "Soubor se nepodařilo načíst.";

        Assert.Equal("Soubor se nepodařilo načíst.", gcode.StatusStripMessage);
    }

    [Fact]
    public void AlignExplainsWhyItIsUnavailableUntilTwoObjectsAreSelected()
    {
        var scene = new SceneViewModel();
        var first = new SceneObject { LocalShapes = [], LocalPivot = Position.Zero, LocalBounds = BoundingBox2D.Empty, Name = "A" };
        var second = new SceneObject { LocalShapes = [], LocalPivot = Position.Zero, LocalBounds = BoundingBox2D.Empty, Name = "B" };
        scene.Execute(new AddObjectCommand(scene.Scene, first, []));
        scene.Execute(new AddObjectCommand(scene.Scene, second, []));

        scene.SelectedObjects.Clear();
        Assert.Equal("Vyberte alespoň dva objekty, aby je šlo zarovnat", scene.AlignDisabledReason);

        scene.SelectedObjects.Add(first);
        Assert.Contains("Vyberte alespoň dva objekty", scene.AlignDisabledReason, StringComparison.Ordinal);

        scene.SelectedObjects.Add(second);
        Assert.Null(scene.AlignDisabledReason);
    }

    // ------------------------------------------------------------------ start summary

    private static StartSummaryInput Summary(bool simulator = false, bool framed = true, string? port = "COM3") =>
        new("Návrh na plátně", 80, 45.5, "xTool S1", port, simulator, "0,0 odpovídá pracovní nule stroje",
            "Výkon: 40 %", "12:30", framed);

    [Fact]
    public void StartSummaryNamesTheMachineAndItsPort()
    {
        var text = StartSummary.Build(Summary());

        Assert.Contains("Zařízení: xTool S1 (COM3)", text, StringComparison.Ordinal);
        Assert.Contains("Úloha: Návrh na plátně", text, StringComparison.Ordinal);
        Assert.Contains("mm", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StartSummaryStatesWhetherPlacementWasFramed()
    {
        Assert.Contains("Rámování: ověřeno", StartSummary.Build(Summary(framed: true)), StringComparison.Ordinal);
        Assert.Contains("Rámování: neověřeno", StartSummary.Build(Summary(framed: false)), StringComparison.Ordinal);
    }

    [Fact]
    public void StartSummaryWarnsThatARealLaserStartsImmediately()
    {
        var text = StartSummary.Build(Summary());

        Assert.Contains("okamžitě začne pracovat", text, StringComparison.Ordinal);
        Assert.DoesNotContain("simulátor", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartSummaryMakesTheSimulatorUnmistakable()
    {
        var text = StartSummary.Build(Summary(simulator: true, port: null));

        Assert.Contains(StartSummary.SimulatorNote, text, StringComparison.Ordinal);
        Assert.Contains("žádný fyzický laser nebude pracovat", text, StringComparison.Ordinal);
        Assert.DoesNotContain("okamžitě začne pracovat", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ actionable errors

    [Fact]
    public void BusyPortSaysWhichProgramMayHoldItAndWhatToDo()
    {
        var text = UserFacingErrors.ConnectionFailed(new UnauthorizedAccessException("Access to the port 'COM3' is denied."), "COM3");

        Assert.Contains("COM3", text, StringComparison.Ordinal);
        Assert.Contains("jiný program", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Access", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPortPointsAtTheCableAndPower()
    {
        var text = UserFacingErrors.ConnectionFailed(new FileNotFoundException("The port 'COM9' does not exist."), "COM9");

        Assert.Contains("USB kabelem", text, StringComparison.Ordinal);
        Assert.DoesNotContain("does not exist", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownConnectionFailureUsesTheGenericActionableSentence()
    {
        var text = UserFacingErrors.ConnectionFailed(new InvalidCastException("Unable to cast object"), "COM3");

        Assert.Equal("LASERO se nepodařilo připojit ke stroji. Zkontrolujte USB kabel a napájení laseru a zkuste to znovu.", text);
    }

    [Fact]
    public void OurOwnCzechMessageSurvivesButFrameworkEnglishNeverLeaks()
    {
        Assert.True(UserFacingErrors.IsOurs("Neplatný soubor SVG: chybí kořenový element."));
        Assert.False(UserFacingErrors.IsOurs("Expected 0/1 flag at position 4, got 'x'."));

        Assert.Equal("Neplatný soubor SVG: chybí kořenový element.",
            UserFacingErrors.FileImportFailed(new InvalidDataException("Neplatný soubor SVG: chybí kořenový element.")));
        var generic = UserFacingErrors.FileImportFailed(new FormatException("Expected 0/1 flag at position 4, got 'x'."));
        Assert.DoesNotContain("Expected", generic, StringComparison.Ordinal);
        Assert.Contains("SVG", generic, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveFailureReassuresThatTheWorkIsInTheAutosave()
    {
        foreach (var error in new Exception[] { new UnauthorizedAccessException(), new DirectoryNotFoundException(), new IOException(), new InvalidOperationException() })
            Assert.Contains("automatické záloze", UserFacingErrors.ProjectSaveFailed(error), StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptProjectSaysTheOriginalWasNotChanged()
    {
        Assert.Contains("Originál nebyl změněn", UserFacingErrors.ProjectOpenFailed(new InvalidDataException()), StringComparison.Ordinal);
        Assert.Contains("Originál nebyl změněn", UserFacingErrors.ProjectOpenFailed(new System.Text.Json.JsonException()), StringComparison.Ordinal);
    }

    [Fact]
    public void LostConnectionDuringAJobSaysItWillNotResume()
    {
        var text = UserFacingErrors.ConnectionLostDuringJob();

        Assert.Contains("neobnoví", text, StringComparison.Ordinal);
        Assert.Contains("USB", UserFacingErrors.ConnectionLost(new IOException("cable")), StringComparison.Ordinal);
    }

    [Fact]
    public void FailedConnectAttemptShowsAShortStatusAndAFullActionableDetail()
    {
        var machine = new GrblConnection(new ThrowingTransport(new UnauthorizedAccessException("Access to the port 'COM7' is denied.")));
        var connection = new ConnectionViewModel(machine, new AppSettingsStore(Path.Combine(_directory, "connect.json")))
        {
            SelectedPort = "COM7",
        };

        connection.ConnectCommand.Execute(null);

        Assert.False(connection.IsConnected);
        Assert.Equal("Připojení selhalo", connection.StatusText);
        Assert.Contains("COM7", connection.StatusDetail, StringComparison.Ordinal);
        Assert.Contains("jiný program", connection.StatusDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("Access", connection.StatusDetail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ brand text rules

    [Fact]
    public void NoGuidanceTextUsesQuestionMarksExclamationMarksOrLongDashes()
    {
        var texts = new List<string>
        {
            GuidanceText.NoSelection, GuidanceText.NoMachine, GuidanceText.EmptyCanvasTitle,
            GuidanceText.EmptyCanvasDescription, StartSummary.SimulatorNote,
            StartSummary.Build(Summary()), StartSummary.Build(Summary(simulator: true, framed: false)),
            UserFacingErrors.ConnectionLost(new IOException()), UserFacingErrors.ConnectionLostDuringJob(),
            UserFacingErrors.RecoveryFailed(new IOException()),
            UserFacingErrors.ConnectionFailed(new TimeoutException(), "COM3"),
            UserFacingErrors.ConnectionFailed(new ArgumentException(), null),
            UserFacingErrors.ConnectionFailed(new IOException("does not exist"), "COM3"),
        };
        foreach (var error in new Exception[] { new UnauthorizedAccessException(), new FileNotFoundException(), new IOException(), new OutOfMemoryException(), new FormatException() })
        {
            texts.Add(UserFacingErrors.FileImportFailed(error));
            texts.Add(UserFacingErrors.ProjectSaveFailed(error));
            texts.Add(UserFacingErrors.ProjectOpenFailed(error));
        }

        foreach (var tool in Enum.GetValues<DesignerTool>())
            foreach (var nodes in new[] { -1, 0, 1, 2 })
                if (GuidanceText.DesignerHint(Context(tool, 2, 0, false, nodes >= 0, Math.Max(nodes, 0))) is { } hint)
                    texts.Add(hint.Text);
        if (GuidanceText.DesignerHint(Context(selected: 1, vectorPath: true)) is { } pathHint) texts.Add(pathHint.Text);

        foreach (var text in texts)
        {
            Assert.DoesNotContain('?', text);
            Assert.DoesNotContain('!', text);
            Assert.DoesNotContain('—', text);
            Assert.DoesNotContain('–', text);
        }
    }

    [Fact]
    public void EveryPreflightSentenceFollowsTheBrandRulesAndSaysWhatToDo()
    {
        var messages = new List<string>();
        var statuses = new[]
        {
            GrblMachineMode.Alarm, GrblMachineMode.Door, GrblMachineMode.Hold,
            GrblMachineMode.Run, GrblMachineMode.Check,
        };

        foreach (var mode in statuses)
        {
            var result = JobPreflight.Evaluate(new JobPreflightContext
            {
                IsConnected = true,
                Document = null,
                MachineStatus = new MachineStatus { Mode = mode, TriggeredPins = "XD" },
                MachineStatusAge = TimeSpan.FromSeconds(5),
                RequireFraming = true,
                HasFramedCurrentDocument = false,
                WorkAreaWidthMm = 400,
                WorkAreaHeightMm = 400,
            });
            messages.AddRange(result.Issues.Select(issue => issue.Message));
        }

        Assert.NotEmpty(messages);
        foreach (var message in messages.Distinct())
        {
            Assert.DoesNotContain('?', message);
            Assert.DoesNotContain('!', message);
            Assert.DoesNotContain('—', message);
            Assert.True(message.EndsWith('.'), $"Preflight sentence should be a full sentence: {message}");
        }

        // Each state a physical machine can be in is distinct in words, not only in colour.
        Assert.Contains(messages, m => m.Contains("alarmu", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("kryt", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("pozastaven", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("pohybuje", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Rámovat", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("USB", StringComparison.Ordinal));
    }

    private sealed class ThrowingTransport : IGrblTransport
    {
        private readonly Exception _error;

        public ThrowingTransport(Exception error) => _error = error;

        public bool IsOpen => false;
#pragma warning disable CS0067
        public event Action<string>? LineReceived;
        public event Action<Exception>? UnexpectedlyClosed;
#pragma warning restore CS0067
        public void Open(string portName, int baudRate) => throw _error;
        public void Close() { }
        public void WriteLine(string text) { }
        public void WriteRealtimeByte(byte value) { }
        public void Dispose() { }
    }
}
