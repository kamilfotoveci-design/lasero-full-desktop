namespace Lasero.App.ViewModels;

/// <summary>
/// One entry in the shape picker: which tool it activates, what its icon and tooltip are, and the
/// keyboard shortcut it already has elsewhere in the app (if any). The toolbar button and the picker
/// both read from <see cref="ShapeToolCatalog"/> rather than each hard-coding the same eight shapes.
/// </summary>
public sealed record ShapeToolDefinition(DesignerTool Tool, string DisplayName, string TooltipText, string IconKey);

/// <summary>The eight closed-shape drawing tools, in the order they appear in the picker.</summary>
public static class ShapeToolCatalog
{
    public static IReadOnlyList<ShapeToolDefinition> All { get; } =
    [
        new(DesignerTool.Rectangle, "Obdélník", "Obdélník (R)", "Glyph.Rectangle"),
        new(DesignerTool.Ellipse, "Elipsa", "Elipsa (E)", "Glyph.Ellipse"),
        new(DesignerTool.Triangle, "Trojúhelník", "Trojúhelník", "Glyph.Triangle"),
        new(DesignerTool.Pentagon, "Pětiúhelník", "Pětiúhelník", "Glyph.Pentagon"),
        new(DesignerTool.Hexagon, "Šestiúhelník", "Šestiúhelník", "Glyph.Hexagon"),
        new(DesignerTool.Octagon, "Osmiúhelník", "Osmiúhelník", "Glyph.Octagon"),
        new(DesignerTool.Star, "Hvězda", "Hvězda", "Glyph.Star"),
        new(DesignerTool.DoubleStar, "Dvojitá hvězda", "Dvojitá hvězda", "Glyph.DoubleStar"),
    ];

    /// <summary>True for the eight closed-shape tools this catalog covers — not Select, Pan, Line or Text.</summary>
    public static bool IsShapeTool(DesignerTool tool) => All.Any(entry => entry.Tool == tool);

    public static ShapeToolDefinition Get(DesignerTool tool) =>
        All.FirstOrDefault(entry => entry.Tool == tool)
        ?? throw new ArgumentOutOfRangeException(nameof(tool), tool, "Not a shape tool.");
}
