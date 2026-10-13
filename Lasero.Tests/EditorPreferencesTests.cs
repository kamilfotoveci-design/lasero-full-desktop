using System.IO;
using Lasero.App;
using Lasero.App.Controls;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// The editor preferences behind Nastavení: defaults equal the behaviour the editor had before the settings
/// existed, a damaged file cannot put the editor out of range, and what a field accepts is one rule shared by the
/// window and the store. The canvas end (hit tolerance, nudge steps, grid) is covered where the logic lives.
/// </summary>
public sealed class EditorPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-editor-prefs-tests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public EditorPreferencesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { /* temp folder */ }
    }

    [Fact]
    public void DefaultsKeepTheBehaviourTheEditorHadBeforeTheSettingsExisted()
    {
        var editor = new EditorPreferences();

        Assert.Equal(TimeSpan.FromSeconds(30), editor.AutosaveInterval);
        Assert.Equal(0.5, editor.ArrowStepFor(shift: false, control: false));
        Assert.Equal(5, editor.ArrowStepFor(shift: true, control: false));
        Assert.Equal(0.05, editor.ArrowStepFor(shift: false, control: true));
        Assert.Equal(SceneHitTester.DefaultTolerancePx, editor.ClickTolerancePx);
        Assert.Equal(10, editor.SnapDistancePx);
        Assert.True(editor.SnapToObjects);
        Assert.True(editor.SnapToGrid);
        Assert.True(editor.ShowGrid);
        Assert.True(editor.AutomaticGridSpacing);
        Assert.True(editor.SmoothEdges);
        Assert.False(editor.ReverseZoomWheel);
        Assert.False(editor.SwitchToSelectAfterDrawing);
        Assert.False(editor.ReduceMotion);
        Assert.True(editor.ZoomsIn(120));
        Assert.False(editor.ZoomsIn(-120));
    }

    [Fact]
    public void ShiftWinsOverCtrlLikeTheCanvasAlwaysDid()
    {
        var editor = new EditorPreferences { ArrowStepMm = 1, ShiftArrowStepMm = 20, CtrlArrowStepMm = 0.1 };

        Assert.Equal(20, editor.ArrowStepFor(shift: true, control: true));
    }

    [Fact]
    public void ReversingTheWheelFlipsEveryNotch()
    {
        var editor = new EditorPreferences { ReverseZoomWheel = true };

        Assert.False(editor.ZoomsIn(120));
        Assert.True(editor.ZoomsIn(-120));
    }

    [Fact]
    public void AutosaveIntervalFollowsTheTypedMinutesAndStaysInRange()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new EditorPreferences { AutosaveMinutes = 5 }.AutosaveInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), new EditorPreferences { AutosaveMinutes = 0 }.AutosaveInterval);
        Assert.Equal(TimeSpan.FromMinutes(60), new EditorPreferences { AutosaveMinutes = 9999 }.AutosaveInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), new EditorPreferences { AutosaveMinutes = double.NaN }.AutosaveInterval);
    }

    [Fact]
    public void AutomaticGridFollowsTheZoomLadderAndAFixedGridKeepsItsSpacing()
    {
        var automatic = new EditorPreferences();
        var fixedGrid = new EditorPreferences { AutomaticGridSpacing = false, GridSpacingMm = 10 };

        Assert.Equal(RulerMath.PickStep(4), automatic.ResolveGridStepMm(4));
        Assert.Equal(RulerMath.PickStep(0.5), automatic.ResolveGridStepMm(0.5));
        Assert.Equal(10, fixedGrid.ResolveGridStepMm(4));
        Assert.Equal(10, fixedGrid.ResolveGridStepMm(40));
    }

    [Fact]
    public void AFixedGridIsCoarsenedByDoublingWhenItsLinesWouldCrowd()
    {
        var fixedGrid = new EditorPreferences { AutomaticGridSpacing = false, GridSpacingMm = 1 };

        // 1 mm at 2 px per mm is 2 px between lines: doubled to 4 mm = 8 px, the closest it is drawn.
        var step = fixedGrid.ResolveGridStepMm(2);

        Assert.Equal(4, step);
        Assert.True(step * 2 >= EditorPreferences.MinGridLinePx);
    }

    [Fact]
    public void NormalizePutsDamagedValuesBackInRange()
    {
        var editor = new EditorPreferences
        {
            AutosaveMinutes = -3, ArrowStepMm = 0, ShiftArrowStepMm = 1e9, CtrlArrowStepMm = double.PositiveInfinity,
            ClickTolerancePx = 0, SnapDistancePx = 500, GridSpacingMm = double.NaN,
        };

        editor.Normalize();

        Assert.Equal(EditorPreferences.MinAutosaveMinutes, editor.AutosaveMinutes);
        Assert.Equal(EditorPreferences.MinNudgeMm, editor.ArrowStepMm);
        Assert.Equal(EditorPreferences.MaxNudgeMm, editor.ShiftArrowStepMm);
        Assert.Equal(0.05, editor.CtrlArrowStepMm);
        Assert.Equal(EditorPreferences.MinClickTolerancePx, editor.ClickTolerancePx);
        Assert.Equal(EditorPreferences.MaxSnapDistancePx, editor.SnapDistancePx);
        Assert.Equal(10, editor.GridSpacingMm);
    }

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("0,5", 0.5)]
    [InlineData(" 12 ", 12)]
    [InlineData("1e1", 10)]
    public void TypedNumbersAcceptAPointOrACommaAndSurroundingSpace(string text, double expected)
    {
        Assert.True(EditorPreferences.TryParseNumber(text, 0.01, 100, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,2,3")]
    public void TypedNumbersTheEditorCannotWorkWithAreRefused(string text)
    {
        Assert.False(EditorPreferences.TryParseNumber(text, 0.01, 100, out _));
    }

    [Fact]
    public void TheFormRefusesABadEntryAndNeverTouchesThePreferences()
    {
        var draft = new EditorPreferences();
        var form = new EditorPreferencesForm(draft);

        var error = Assert.Throws<ArgumentException>(() => form.ClickTolerancePx = "500");
        Assert.Contains("px", error.Message);
        Assert.Equal(6, draft.ClickTolerancePx);

        form.ClickTolerancePx = "8,5";
        Assert.Equal(8.5, draft.ClickTolerancePx);
        Assert.Equal("8.5", form.ClickTolerancePx);
    }

    [Fact]
    public void EditorPreferencesRoundTripThroughTheSettingsFile()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Current.Editor.AutosaveMinutes = 5;
        store.Current.Editor.ReverseZoomWheel = true;
        store.Current.Editor.SnapToGrid = false;
        store.Current.Editor.AutomaticGridSpacing = false;
        store.Current.Editor.GridSpacingMm = 2.5;
        store.Current.Editor.ReduceMotion = true;
        store.Save();

        var loaded = new AppSettingsStore(SettingsPath).Load().Editor;

        Assert.Equal(5, loaded.AutosaveMinutes);
        Assert.True(loaded.ReverseZoomWheel);
        Assert.False(loaded.SnapToGrid);
        Assert.False(loaded.AutomaticGridSpacing);
        Assert.Equal(2.5, loaded.GridSpacingMm);
        Assert.True(loaded.ReduceMotion);
    }

    [Fact]
    public void ASettingsFileFromBeforeTheEditorSectionLoadsTheDefaults()
    {
        File.WriteAllText(SettingsPath, "{\"Workspace\":{\"InspectorWidth\":400}}");

        var editor = new AppSettingsStore(SettingsPath).Load().Editor;

        Assert.Equal(TimeSpan.FromSeconds(30), editor.AutosaveInterval);
        Assert.Equal(0.5, editor.ArrowStepMm);
    }

    [Fact]
    public void ANullOrOutOfRangeEditorSectionIsRepairedOnLoad()
    {
        File.WriteAllText(SettingsPath, "{\"Editor\":null}");
        Assert.NotNull(new AppSettingsStore(SettingsPath).Load().Editor);

        File.WriteAllText(SettingsPath, "{\"Editor\":{\"AutosaveMinutes\":0,\"ClickTolerancePx\":900,\"ArrowStepMm\":-1}}");
        var editor = new AppSettingsStore(SettingsPath).Load().Editor;

        Assert.Equal(EditorPreferences.MinAutosaveMinutes, editor.AutosaveMinutes);
        Assert.Equal(EditorPreferences.MaxClickTolerancePx, editor.ClickTolerancePx);
        Assert.Equal(EditorPreferences.MinNudgeMm, editor.ArrowStepMm);
    }

    [Fact]
    public void CloneIsIndependentOfTheOriginal()
    {
        var original = new EditorPreferences { ArrowStepMm = 2 };
        var copy = original.Clone();

        copy.ArrowStepMm = 9;

        Assert.Equal(2, original.ArrowStepMm);
    }

    [Theory]
    [InlineData(6, 5.5, true)]
    [InlineData(6, 7.5, false)]
    [InlineData(12, 11.5, true)]
    [InlineData(2, 2.5, false)]
    public void ClickToleranceFollowsThePreferenceInScreenPixels(double tolerancePx, double missPx, bool selected)
    {
        const double pixelsPerMm = 2;
        var line = new SceneObject
        {
            Name = "Line",
            LocalShapes =
            [
                new ImportedShape
                {
                    Points = [new Position(0, 0, 0), new Position(100, 0, 0)],
                    IsClosed = false,
                    LayerColor = RgbColor.Black,
                    PreferredMode = LayerMode.Cut,
                },
            ],
            LocalBounds = BoundingBox2D.Empty.Include(0, 0).Include(100, 0),
            LocalPivot = new Position(50, 0, 0),
        };

        var hits = SceneHitTester.HitTest(
            [line], new Position(50, missPx / pixelsPerMm, 0), pixelsPerMm, (_, _) => false, tolerancePx: tolerancePx);

        Assert.Equal(selected, hits.Count == 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(double.NaN)]
    public void AHitToleranceThatIsNotPositiveFallsBackToTheDefault(double tolerancePx)
    {
        var square = new SceneObject
        {
            Name = "Square",
            LocalShapes =
            [
                new ImportedShape
                {
                    Points = [new Position(0, 0, 0), new Position(100, 0, 0)],
                    IsClosed = false,
                    LayerColor = RgbColor.Black,
                    PreferredMode = LayerMode.Cut,
                },
            ],
            LocalBounds = BoundingBox2D.Empty.Include(0, 0).Include(100, 0),
            LocalPivot = new Position(50, 0, 0),
        };

        Assert.Single(SceneHitTester.HitTest([square], new Position(50, 2.5, 0), 1, (_, _) => false, tolerancePx: tolerancePx));
    }
}
