using CommunityToolkit.Mvvm.ComponentModel;

namespace Lasero.Core.Layers;

/// <summary>
/// One stable processing layer, mirroring LightBurn's "Cuts/Layers" list. Geometry references
/// the layer by ID; color remains its visual label and an import compatibility hint. Defaults mirror lasero-app's own
/// lightburn-export.js DEFAULTS so numbers feel familiar to existing users.
/// </summary>
public sealed partial class LayerSettings : ObservableObject
{
    /// <summary>Stable project identity. Color remains a visual identifier, not the layer's identity.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    [ObservableProperty] private RgbColor _color;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private LayerMode _mode = LayerMode.Cut;
    [ObservableProperty] private double _speed = 350;
    [ObservableProperty] private double _power = 95;
    [ObservableProperty] private int _passes = 1;

    /// <summary>Fill-mode scan line spacing in mm (25.4 / DPI) — irrelevant for Cut layers.</summary>
    [ObservableProperty] private double _fillLineIntervalMm = 25.4 / 254;

    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isRaster;

    public string ColorHex => Color.ToHex();
    public string ModeLabel => IsRaster ? "Obrázek" : Mode switch
    {
        LayerMode.Cut => "Čára",
        LayerMode.Fill => "Výplň",
        LayerMode.FillAndCut => "Výplň + čára",
        _ => "Neznámý režim",
    };
    public string ProcessingSummary => $"{ModeLabel} · {Speed:0} mm/min · {Power:0.#} % · {Passes}×";

    partial void OnColorChanged(RgbColor value) => OnPropertyChanged(nameof(ColorHex));
    partial void OnModeChanging(LayerMode oldValue, LayerMode newValue)
    {
        // Cutting and filling are not the same operation at the same speed. Cut runs slow and hot to
        // get through material; fill runs fast and light to darken a surface. Switching mode used to
        // keep whatever speed was there, so a layer moved from Čára to Výplň kept its 350 mm/min
        // cutting feed and the job estimate ballooned into hours for a palm-sized fill.
        //
        // Only values the operator has not touched are migrated: if Speed/Power still match the old
        // mode's defaults they move to the new mode's defaults, and anything deliberately typed in is
        // left exactly as it is.
        var previous = DefaultsFor(oldValue);
        var next = DefaultsFor(newValue);

        if (AreClose(Speed, previous.Speed)) Speed = next.Speed;
        if (AreClose(Power, previous.Power)) Power = next.Power;
    }

    partial void OnModeChanged(LayerMode value)
    {
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(ProcessingSummary));
    }

    partial void OnIsRasterChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(ProcessingSummary));
    }

    private static (double Speed, double Power) DefaultsFor(LayerMode mode) => mode == LayerMode.Cut
        ? (350d, 95d)
        : (3000d, 30d);

    private static bool AreClose(double a, double b) => Math.Abs(a - b) < 0.001;
    partial void OnSpeedChanged(double value) => OnPropertyChanged(nameof(ProcessingSummary));
    partial void OnPowerChanged(double value) => OnPropertyChanged(nameof(ProcessingSummary));
    partial void OnPassesChanged(int value) => OnPropertyChanged(nameof(ProcessingSummary));

    public override string ToString() => Name;

    public static LayerSettings CreateDefault(RgbColor color, LayerMode mode, string name) => mode == LayerMode.Cut
        ? new LayerSettings { Color = color, Name = name, Mode = LayerMode.Cut, Speed = 350, Power = 95, Passes = 1 }
        : new LayerSettings { Color = color, Name = name, Mode = mode, Speed = 3000, Power = 30, Passes = 1, FillLineIntervalMm = 25.4 / 254 };
}
