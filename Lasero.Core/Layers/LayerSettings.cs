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

    /// <summary>
    /// Name of the material recipe these numbers came from, or null when the operator typed them in.
    /// Presentation only — nothing in the toolpath reads it. It exists because Speed/Power/Passes on
    /// their own cannot say <em>what</em> they were meant for, so a layer set from the catalogue and
    /// a layer someone guessed at looked identical once the dialog closed.
    /// </summary>
    [ObservableProperty] private string? _materialLabel;

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

    /// <summary>Whether the line-spacing interval means anything for this layer — a pure outline
    /// cut never scans lines, so its interval field has nothing to control.</summary>
    public bool UsesFillInterval => IsRaster || Mode is LayerMode.Fill or LayerMode.FillAndCut;

    /// <summary>What the material row shows when no recipe has been applied.</summary>
    public string MaterialDisplayLabel => MaterialLabel is { Length: > 0 } label ? label : "Vlastní nastavení";

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
        OnPropertyChanged(nameof(UsesFillInterval));
    }

    partial void OnIsRasterChanged(bool value)
    {
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(ProcessingSummary));
        OnPropertyChanged(nameof(UsesFillInterval));
    }

    private static (double Speed, double Power) DefaultsFor(LayerMode mode) => mode == LayerMode.Cut
        ? (350d, 95d)
        : (3000d, 30d);

    private static bool AreClose(double a, double b) => Math.Abs(a - b) < 0.001;
    partial void OnSpeedChanged(double value)
    {
        OnPropertyChanged(nameof(ProcessingSummary));
        ForgetMaterial();
    }

    partial void OnPowerChanged(double value)
    {
        OnPropertyChanged(nameof(ProcessingSummary));
        ForgetMaterial();
    }

    partial void OnPassesChanged(int value)
    {
        OnPropertyChanged(nameof(ProcessingSummary));
        ForgetMaterial();
    }

    partial void OnFillLineIntervalMmChanged(double value) => ForgetMaterial();
    partial void OnMaterialLabelChanged(string? value) => OnPropertyChanged(nameof(MaterialDisplayLabel));

    /// <summary>
    /// Applies a catalogue recipe or a saved preset as one unit and records where it came from.
    /// It has to be one call rather than five assignments: every individual setter drops the
    /// material name (see <see cref="ForgetMaterial"/>), so assigning the numbers one by one would
    /// erase the label the recipe is trying to set.
    /// </summary>
    public void ApplyRecipe(LayerMode mode, double speed, double power, int passes, double fillLineIntervalMm, string? materialLabel)
    {
        _applyingRecipe = true;
        try
        {
            Mode = mode;
            Speed = speed;
            Power = power;
            Passes = passes;
            FillLineIntervalMm = fillLineIntervalMm;
        }
        finally
        {
            _applyingRecipe = false;
        }

        MaterialLabel = materialLabel;
    }

    /// <summary>
    /// A hand-typed number means the layer no longer holds the recipe it claims to. Keeping the name
    /// would be worse than showing none: the operator would read "Překližka 3 mm" off a layer whose
    /// power they had just halved, and trust a figure nothing stands behind.
    /// </summary>
    private void ForgetMaterial()
    {
        if (_applyingRecipe) return;
        MaterialLabel = null;
    }

    private bool _applyingRecipe;

    public override string ToString() => Name;

    public static LayerSettings CreateDefault(RgbColor color, LayerMode mode, string name) => mode == LayerMode.Cut
        ? new LayerSettings { Color = color, Name = name, Mode = LayerMode.Cut, Speed = 350, Power = 95, Passes = 1 }
        : new LayerSettings { Color = color, Name = name, Mode = mode, Speed = 3000, Power = 30, Passes = 1, FillLineIntervalMm = 25.4 / 254 };
}
