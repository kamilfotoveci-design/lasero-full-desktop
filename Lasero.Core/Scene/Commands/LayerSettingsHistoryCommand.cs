using Lasero.Core.Layers;

namespace Lasero.Core.Scene.Commands;

/// <summary>One inspector edit, including mode's automatic speed/power changes and material label.</summary>
public sealed class LayerSettingsHistoryCommand : ISceneCommand
{
    private readonly LayerSettings _layer;
    private readonly LayerSettingsState _before;
    private readonly LayerSettingsState _after;

    public LayerSettingsHistoryCommand(LayerSettings layer, LayerSettingsState before, LayerSettingsState after)
    {
        _layer = layer;
        _before = before;
        _after = after;
    }

    public void Do() => _after.Apply(_layer);
    public void Undo() => _before.Apply(_layer);
}

public sealed record LayerSettingsState(
    RgbColor Color, string Name, LayerMode Mode, double Speed, double Power, int Passes,
    double FillLineIntervalMm, string? MaterialLabel, bool IsEnabled, bool IsVisible,
    bool IsLocked, bool IsRaster)
{
    public static LayerSettingsState Capture(LayerSettings layer) => new(
        layer.Color, layer.Name, layer.Mode, layer.Speed, layer.Power, layer.Passes,
        layer.FillLineIntervalMm, layer.MaterialLabel, layer.IsEnabled, layer.IsVisible,
        layer.IsLocked, layer.IsRaster);

    public void Apply(LayerSettings layer)
    {
        layer.Color = Color;
        layer.Name = Name;
        layer.Mode = Mode;
        layer.Speed = Speed;
        layer.Power = Power;
        layer.Passes = Passes;
        layer.FillLineIntervalMm = FillLineIntervalMm;
        layer.IsEnabled = IsEnabled;
        layer.IsVisible = IsVisible;
        layer.IsLocked = IsLocked;
        layer.IsRaster = IsRaster;
        layer.MaterialLabel = MaterialLabel;
    }
}
