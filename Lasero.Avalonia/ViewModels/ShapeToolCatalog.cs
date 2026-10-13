namespace Lasero.Avalonia.ViewModels;

/// <summary>One entry in the shape picker. Matches Lasero.App's ShapeToolDefinition/ShapeToolCatalog
/// verbatim (same order, same Czech names, same icon keys) so the picker reads identically.</summary>
public sealed record ShapeToolDefinition(ShapeKind Shape, string DisplayName, string TooltipText, string IconKey);

public static class ShapeToolCatalog
{
    public static IReadOnlyList<ShapeToolDefinition> All { get; } =
    [
        new(ShapeKind.Rectangle, "Obdélník", "Obdélník (R)", "Glyph.Rectangle"),
        new(ShapeKind.Ellipse, "Elipsa", "Elipsa (E)", "Glyph.Ellipse"),
        new(ShapeKind.Triangle, "Trojúhelník", "Trojúhelník", "Glyph.Triangle"),
        new(ShapeKind.Pentagon, "Pětiúhelník", "Pětiúhelník", "Glyph.Pentagon"),
        new(ShapeKind.Hexagon, "Šestiúhelník", "Šestiúhelník", "Glyph.Hexagon"),
        new(ShapeKind.Octagon, "Osmiúhelník", "Osmiúhelník", "Glyph.Octagon"),
        new(ShapeKind.Star, "Hvězda", "Hvězda", "Glyph.Star"),
        new(ShapeKind.DoubleStar, "Dvojitá hvězda", "Dvojitá hvězda", "Glyph.DoubleStar"),
    ];

    public static ShapeToolDefinition Get(ShapeKind shape) =>
        All.First(entry => entry.Shape == shape);
}
