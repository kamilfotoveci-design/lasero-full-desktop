using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Tests;

/// <summary>
/// Where text ends up after its wording changes. The inline editor grows to the right from the point
/// the text starts at; the committed object has to start at that same point, or the letters jump by
/// half of whatever was typed the moment the edit is committed.
/// </summary>
public sealed class TextAnchoringTests
{
    private static readonly RgbColor Color = new(52, 52, 52);
    private const double Tolerance = 1e-6;

    private static SceneObject Placed(string text, ObjectTransform? transform = null)
    {
        var obj = VectorTextFactory.Create(new TextSource { Text = text, HeightMm = 12 }, new Position(50, 60, 0), Color);
        if (transform is { } t) obj.Transform = t;
        return obj;
    }

    private static Position StartOf(SceneObject text) =>
        text.Transform.Apply(VectorTextFactory.LayoutOriginLocal(text.Text!), text.LocalPivot);

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(30, 1, 1)]
    [InlineData(90, 2, 2)]
    [InlineData(-25, 2.5, 2.5)]
    [InlineData(0, -1, 1)]
    public void ChangedWordingKeepsTheStartOfTheTextWhereItWas(double rotation, double scaleX, double scaleY)
    {
        var original = Placed("TEXT", ObjectTransform.Identity with { X = 80, Y = 40, RotationDeg = rotation, ScaleX = scaleX, ScaleY = scaleY });

        var longer = VectorTextFactory.RebuildKeepingStart(original, original.Text! with { Text = "TEXT TEXT TEXT" });
        var shorter = VectorTextFactory.RebuildKeepingStart(original, original.Text! with { Text = "T" });

        var before = StartOf(original);
        Assert.Equal(before.X, StartOf(longer).X, 5);
        Assert.Equal(before.Y, StartOf(longer).Y, 5);
        Assert.Equal(before.X, StartOf(shorter).X, 5);
        Assert.Equal(before.Y, StartOf(shorter).Y, 5);
    }

    [Fact]
    public void PlainRebuildWouldHaveMovedTheStart()
    {
        // Guards the test above: if Rebuild already held the start still, RebuildKeepingStart would be
        // pointless and the assertions would prove nothing.
        var original = Placed("TEXT");

        var plain = VectorTextFactory.Rebuild(original, original.Text! with { Text = "TEXT TEXT TEXT" });

        var moved = Math.Abs(StartOf(plain).X - StartOf(original).X);
        Assert.True(moved > 5, $"plain Rebuild moved the start by only {moved:0.00} mm");
    }

    [Fact]
    public void FirstLetterInkStaysPutWhenTheRestOfTheWordingChanges()
    {
        var original = Placed("TEXT");

        var longer = VectorTextFactory.RebuildKeepingStart(original, original.Text! with { Text = "TEXTILE" });

        // Same first letter, unrotated and unscaled: the left edge of the ink is the left edge of the "T".
        Assert.Equal(original.WorldBounds().MinX, longer.WorldBounds().MinX, 2);
        Assert.True(longer.WorldBounds().MaxX > original.WorldBounds().MaxX, "the longer wording grows to the right");
    }

    [Fact]
    public void RewordingKeepsScaleRotationIdentityAndTheCentredPivot()
    {
        var original = Placed("TEXT", ObjectTransform.Identity with { X = 80, Y = 40, RotationDeg = 33, ScaleX = 1.5, ScaleY = 1.5 });

        var rebuilt = VectorTextFactory.RebuildKeepingStart(original, original.Text! with { Text = "Nazdar" });

        Assert.Equal(original.Id, rebuilt.Id);
        Assert.Equal(33, rebuilt.Transform.RotationDeg);
        Assert.Equal(1.5, rebuilt.Transform.ScaleX);
        Assert.Equal(1.5, rebuilt.Transform.ScaleY);
        Assert.True(rebuilt.IsPivotAtBoundsCenter, "the pivot has to stay at the centre of the bounds");
        // The type size is the object's own scale times the text height: rewording must not rescale it.
        Assert.Equal(original.Text!.HeightMm, rebuilt.Text!.HeightMm);
    }

    [Fact]
    public void WarpedTextFallsBackToKeepingTheCentre()
    {
        var warped = VectorTextFactory.Create(
            new TextSource { Text = "HHHH", HeightMm = 20, Distortion = TextDistortion.None with { TopLeftX = 0.4, TopRightX = 1.4 } },
            new Position(10, 10, 0), Color);
        warped.Transform = warped.Transform with { X = 30, Y = 30, RotationDeg = 10 };

        var rebuilt = VectorTextFactory.RebuildKeepingStart(warped, warped.Text! with { Text = "HH" });

        Assert.Equal(warped.Transform, rebuilt.Transform);
    }

    [Fact]
    public void CommitTextEditKeepsTheStartAndIsOneUndoStep()
    {
        var viewModel = new SceneViewModel();
        viewModel.AddText("TEXT", new Position(50, 60, 0), 12);
        var created = Assert.Single(viewModel.Objects);
        created.Transform = created.Transform with { RotationDeg = 20 };
        var start = StartOf(created);
        var transformBefore = created.Transform;

        viewModel.CommitTextEdit(created, "TEXT TEXT TEXT");

        var edited = Assert.Single(viewModel.Objects);
        Assert.Equal(start.X, StartOf(edited).X, 5);
        Assert.Equal(start.Y, StartOf(edited).Y, 5);

        viewModel.UndoCommand.Execute(null);
        var restored = Assert.Single(viewModel.Objects);
        Assert.Equal("TEXT", restored.Text!.Text);
        Assert.Equal(transformBefore, restored.Transform);
        Assert.True(viewModel.CanUndo, "undoing the edit must leave the creation on the stack, one step at a time");
    }

    [Fact]
    public void ChangingTheFontKeepsTheMiddleNotTheStart()
    {
        var viewModel = new SceneViewModel();
        viewModel.AddText("TEXT", new Position(50, 60, 0), 12);
        var created = Assert.Single(viewModel.Objects);
        var centre = created.Transform;

        viewModel.SelectedTextHeight = 24;

        var rebuilt = Assert.Single(viewModel.Objects);
        Assert.Equal(centre.X, rebuilt.Transform.X, 6);
        Assert.Equal(centre.Y, rebuilt.Transform.Y, 6);
    }
}
