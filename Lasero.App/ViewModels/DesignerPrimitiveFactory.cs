using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.App.ViewModels;

internal static class DesignerPrimitiveFactory
{
    public static SceneObject Create(DesignerTool tool, Position start, Position end, RgbColor color) => tool switch
    {
        DesignerTool.Rectangle => ScenePrimitiveFactory.CreateRectangle(start, end, color, "Obdélník"),
        DesignerTool.Ellipse => ScenePrimitiveFactory.CreateEllipse(start, end, color, "Elipsa"),
        DesignerTool.Line => ScenePrimitiveFactory.CreateLine(start, end, color, "Čára"),
        DesignerTool.Triangle => ScenePrimitiveFactory.CreatePolygon(start, end, 3, color, "Trojúhelník"),
        DesignerTool.Pentagon => ScenePrimitiveFactory.CreatePolygon(start, end, 5, color, "Pětiúhelník"),
        DesignerTool.Hexagon => ScenePrimitiveFactory.CreatePolygon(start, end, 6, color, "Šestiúhelník"),
        DesignerTool.Octagon => ScenePrimitiveFactory.CreatePolygon(start, end, 8, color, "Osmiúhelník"),
        DesignerTool.Star => ScenePrimitiveFactory.CreateStar(start, end, 5, 0.45, color, "Hvězda"),
        DesignerTool.DoubleStar => ScenePrimitiveFactory.CreateStar(start, end, 8, 0.58, color, "Dvojitá hvězda"),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Tento nástroj nevytváří vektorový tvar."),
    };

    public static bool IsPrimitive(DesignerTool tool) => tool is
        DesignerTool.Rectangle or DesignerTool.Ellipse or DesignerTool.Line or
        DesignerTool.Triangle or DesignerTool.Pentagon or DesignerTool.Hexagon or
        DesignerTool.Octagon or DesignerTool.Star or DesignerTool.DoubleStar;
}
