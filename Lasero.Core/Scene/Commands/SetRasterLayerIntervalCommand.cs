using Lasero.Core.Layers;

namespace Lasero.Core.Scene.Commands;

/// <summary>Changes raster scan spacing as one undoable layer operation.</summary>
public sealed class SetRasterLayerIntervalCommand : ISceneCommand
{
    private readonly LayerSettings _layer;
    private readonly double _before;
    private readonly double _after;

    public SetRasterLayerIntervalCommand(LayerSettings layer, double after)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (!double.IsFinite(after) || after <= 0) throw new ArgumentOutOfRangeException(nameof(after));
        _layer = layer;
        _before = layer.FillLineIntervalMm;
        _after = after;
    }

    public void Do() => _layer.FillLineIntervalMm = _after;
    public void Undo() => _layer.FillLineIntervalMm = _before;
}
