using System.Globalization;
using System.IO;
using Lasero.App;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Tests;

/// <summary>
/// Text used to be flattened to curves on creation, so the font could never be changed again. These
/// pin the part that makes it editable: the object keeps what it says and how it is set, a change
/// re-renders the contours without moving the text or losing its layer, and a project saved before
/// any of this existed still loads.
/// </summary>
public sealed class EditableTextTests
{
    private static readonly RgbColor Color = new(52, 52, 52);

    [Fact]
    public void CreatedTextRemembersWhatItSaysAndHowItIsSet()
    {
        var source = new TextSource
        {
            Text = "Lasero",
            HeightMm = 18,
            FontFamily = "Segoe UI",
            Bold = true,
        };

        var text = VectorTextFactory.Create(source, new Position(10, 20, 0), Color);

        Assert.True(text.IsText);
        Assert.Equal("Lasero", text.Text!.Text);
        Assert.Equal(18, text.Text.HeightMm);
        Assert.True(text.Text.Bold);
        Assert.NotEmpty(text.LocalShapes);
    }

    [Fact]
    public void RebuildKeepsIdentityPlacementAndLayer()
    {
        var original = VectorTextFactory.Create(
            new TextSource { Text = "Ahoj", HeightMm = 12 }, new Position(0, 0, 0), Color);
        original.Transform = original.Transform with { X = 40, Y = 55, RotationDeg = 15, ScaleX = 2, ScaleY = 2 };
        var layerId = Guid.NewGuid();
        original.LocalShapes = original.LocalShapes.Select(shape => shape with { LayerId = layerId }).ToList();

        var rebuilt = VectorTextFactory.Rebuild(original, original.Text! with { Text = "Nazdar" });

        Assert.Equal(original.Id, rebuilt.Id);
        Assert.Equal(original.Transform, rebuilt.Transform);
        Assert.All(rebuilt.LocalShapes, shape => Assert.Equal(layerId, shape.LayerId));
        Assert.Equal("Nazdar", rebuilt.Text!.Text);
        Assert.Equal("Nazdar", rebuilt.Name);
    }

    [Fact]
    public void UppercaseChangesTheOutlinesWithoutLosingTheTypedWording()
    {
        var source = new TextSource { Text = "ahoj", HeightMm = 12, Uppercase = true };

        var text = VectorTextFactory.Create(source, Position.Zero, Color);

        // The wording the operator typed has to survive, or turning capitals back off would lose it.
        Assert.Equal("ahoj", text.Text!.Text);
        Assert.Equal("AHOJ", text.Text.EffectiveText(CultureInfo.GetCultureInfo("cs-CZ")));
        Assert.Equal("AHOJ", text.Name);
    }

    [Fact]
    public void WeldMergesOverlappingLetterOutlinesIntoFewerContours()
    {
        // A tight negative-tracking script face is the case weld exists for. Segoe Script overlaps its
        // letters at this size, so welding must not leave the buried edges as separate contours.
        var plain = new TextSource { Text = "elle", HeightMm = 40, FontFamily = "Segoe Script" };

        var loose = VectorTextFactory.Create(plain, Position.Zero, Color);
        var welded = VectorTextFactory.Create(plain with { Weld = true }, Position.Zero, Color);

        Assert.True(welded.LocalShapes.Count <= loose.LocalShapes.Count,
            $"Welding produced more contours ({welded.LocalShapes.Count}) than it started with ({loose.LocalShapes.Count}).");
        Assert.NotEmpty(welded.LocalShapes);
    }

    [Fact]
    public void DistortionWarpsTheOutlinesAndResetsBackToTheOriginalBox()
    {
        var upright = new TextSource { Text = "HHHH", HeightMm = 20 };
        var straight = VectorTextFactory.Create(upright, Position.Zero, Color);

        // Pull the top edge to the right: a slant, expressed as two moved corners.
        var slanted = VectorTextFactory.Create(
            upright with
            {
                Distortion = TextDistortion.None with { TopLeftX = 0.4, TopRightX = 1.4 },
            },
            Position.Zero,
            Color);

        Assert.True(slanted.LocalBounds.Width > straight.LocalBounds.Width,
            "Slanting the top of the text has to make its box wider.");
        Assert.False(slanted.Text!.Distortion.IsIdentity);

        var reset = VectorTextFactory.Rebuild(slanted, slanted.Text with { Distortion = TextDistortion.None });
        Assert.True(reset.Text!.Distortion.IsIdentity);
        Assert.Equal(straight.LocalBounds.Width, reset.LocalBounds.Width, 3);
    }

    [Fact]
    public void DistortionMapsTheUnitBoxThroughItsCorners()
    {
        Assert.Equal((0.5, 0.5), TextDistortion.None.Map(0.5, 0.5));

        var pulled = TextDistortion.None with { TopRightX = 2 };
        var (x, y) = pulled.Map(1, 1);
        Assert.Equal(2, x, 6);
        Assert.Equal(1, y, 6);

        // A corner out of range would flatten the text to nothing or smear it beyond use, so a hand
        // edited project file has to be rejected rather than rendered.
        Assert.False((TextDistortion.None with { TopRightX = double.NaN }).IsUsable);
        Assert.False((TextDistortion.None with { TopRightX = 99 }).IsUsable);
        Assert.True(pulled.IsUsable);
    }

    [Fact]
    public void HeightOutsideTheAllowedRangeIsRejectedBeforeAnythingIsBuilt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VectorTextFactory.Create(new TextSource { Text = "A", HeightMm = 0.5 }, Position.Zero, Color));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VectorTextFactory.Create(new TextSource { Text = "A", HeightMm = 500 }, Position.Zero, Color));
        Assert.Throws<ArgumentException>(() =>
            VectorTextFactory.Create(new TextSource { Text = "   ", HeightMm = 12 }, Position.Zero, Color));
    }

    [Fact]
    public void RebuildRefusesAnObjectThatIsNotText()
    {
        var notText = VectorTextFactory.Create(
            new TextSource { Text = "X", HeightMm = 12 }, Position.Zero, Color);
        var stripped = new SceneObject
        {
            LocalShapes = notText.LocalShapes,
            LocalPivot = notText.LocalPivot,
            LocalBounds = notText.LocalBounds,
        };

        Assert.Throws<InvalidOperationException>(() =>
            VectorTextFactory.Rebuild(stripped, new TextSource { Text = "Y", HeightMm = 12 }));
    }

    [Fact]
    public void TextSurvivesAProjectRoundTrip()
    {
        var project = new LaseroProjectFile
        {
            Objects =
            [
                new ProjectObject
                {
                    Name = "Lasero",
                    Text = new TextSource
                    {
                        Text = "Lasero",
                        HeightMm = 24,
                        FontFamily = "Consolas",
                        Bold = true,
                        Uppercase = true,
                        Weld = true,
                        Distortion = TextDistortion.None with { TopRightX = 1.3 },
                    },
                },
            ],
        };

        var restored = ProjectFileSerializer.Deserialize(ProjectFileSerializer.Serialize(project));

        var text = Assert.Single(restored.Objects).Text;
        Assert.NotNull(text);
        Assert.Equal("Lasero", text.Text);
        Assert.Equal(24, text.HeightMm);
        Assert.Equal("Consolas", text.FontFamily);
        Assert.True(text.Bold);
        Assert.True(text.Uppercase);
        Assert.True(text.Weld);
        Assert.Equal(1.3, text.Distortion.TopRightX, 6);
    }

    [Fact]
    public void ProjectsSavedBeforeTextBecameEditableStillLoad()
    {
        // No "Text" member at all — the shape a project written by an earlier build has. It must load
        // as plain curves rather than failing, because there is no wording to recover from a
        // flattened outline anyway.
        const string json = """
        {
          "Version": 6,
          "Name": "Starý projekt",
          "Objects": [
            {
              "Name": "Text",
              "Shapes": [
                {
                  "Points": [ { "X": 0, "Y": 0, "Z": 0 }, { "X": 5, "Y": 0, "Z": 0 } ],
                  "IsClosed": false,
                  "LayerColor": { "R": 52, "G": 52, "B": 52 },
                  "PreferredMode": "Fill"
                }
              ]
            }
          ],
          "Layers": []
        }
        """;

        var project = ProjectFileSerializer.Deserialize(json);

        var item = Assert.Single(project.Objects);
        Assert.Null(item.Text);
        Assert.Single(item.Shapes);
    }
}
