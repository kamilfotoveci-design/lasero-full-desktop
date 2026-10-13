using System.Globalization;

namespace Lasero.App;

/// <summary>
/// The editable face of <see cref="EditorPreferences"/> for Nastavení. The window edits a copy and only a
/// confirmed "Uložit a zavřít" replaces the live preferences, so closing the window another way leaves the
/// editor exactly as it was. Numbers are typed text: a value the editor cannot work with throws from the
/// setter, which WPF turns into a validation error on that field (and Enter or Esc puts the last good value
/// back), so a bad entry can never reach the preferences.
/// </summary>
public sealed class EditorPreferencesForm(EditorPreferences draft)
{
    public EditorPreferences Draft { get; } = draft;

    public string AutosaveMinutes
    {
        get => Show(Draft.AutosaveMinutes);
        set => Draft.AutosaveMinutes = Read(value, "Interval zálohy", EditorPreferences.MinAutosaveMinutes, EditorPreferences.MaxAutosaveMinutes, "min");
    }

    public string ClickTolerancePx
    {
        get => Show(Draft.ClickTolerancePx);
        set => Draft.ClickTolerancePx = Read(value, "Tolerance kliknutí", EditorPreferences.MinClickTolerancePx, EditorPreferences.MaxClickTolerancePx, "px");
    }

    public string SnapDistancePx
    {
        get => Show(Draft.SnapDistancePx);
        set => Draft.SnapDistancePx = Read(value, "Dosah přichytávání", EditorPreferences.MinSnapDistancePx, EditorPreferences.MaxSnapDistancePx, "px");
    }

    public string GridSpacingMm
    {
        get => Show(Draft.GridSpacingMm);
        set => Draft.GridSpacingMm = Read(value, "Rozteč mřížky", EditorPreferences.MinGridSpacingMm, EditorPreferences.MaxGridSpacingMm, "mm");
    }

    public string ArrowStepMm
    {
        get => Show(Draft.ArrowStepMm);
        set => Draft.ArrowStepMm = Read(value, "Posun šipkou", EditorPreferences.MinNudgeMm, EditorPreferences.MaxNudgeMm, "mm");
    }

    public string ShiftArrowStepMm
    {
        get => Show(Draft.ShiftArrowStepMm);
        set => Draft.ShiftArrowStepMm = Read(value, "Posun šipkou se Shiftem", EditorPreferences.MinNudgeMm, EditorPreferences.MaxNudgeMm, "mm");
    }

    public string CtrlArrowStepMm
    {
        get => Show(Draft.CtrlArrowStepMm);
        set => Draft.CtrlArrowStepMm = Read(value, "Posun šipkou s Ctrl", EditorPreferences.MinNudgeMm, EditorPreferences.MaxNudgeMm, "mm");
    }

    /// <summary>Shown the way the other numeric fields of the app show a number: a point, no trailing zeros.</summary>
    public static string Show(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The sentence a field shows when its value is refused.</summary>
    public static string RangeMessage(string label, double min, double max, string unit) =>
        $"{label}: povolená hodnota je {Show(min)} až {Show(max)} {unit}.";

    private static double Read(string? text, string label, double min, double max, string unit)
    {
        if (EditorPreferences.TryParseNumber(text, min, max, out var value)) return value;
        throw new ArgumentException(RangeMessage(label, min, max, unit));
    }
}
