using System.Windows.Media;

namespace Lasero.App.Controls;

/// <summary>
/// The canvas end of Nastavení: it reads <see cref="EditorPreferences"/> (hit tolerance, snap distance,
/// nudge steps, grid, edge smoothing). Everything here is screen-space interaction behaviour; none of it
/// is stored in the project or reaches the toolpath.
/// </summary>
public partial class SceneCanvas
{
    private EditorPreferences _preferences = new();

    /// <summary>The live preference object. MainWindow assigns it at start-up and again after Nastavení
    /// closes, so a changed value is picked up without recreating the canvas.</summary>
    public EditorPreferences Preferences
    {
        get => _preferences;
        set
        {
            _preferences = value ?? new EditorPreferences();
            ApplyEditorPreferences();
        }
    }

    private void ApplyEditorPreferences()
    {
        // The edge mode is inherited by every visual under the canvas, so one write covers the whole scene.
        RenderOptions.SetEdgeMode(DrawCanvas, _preferences.SmoothEdges ? EdgeMode.Unspecified : EdgeMode.Aliased);
        RedrawGrid();
    }
}
