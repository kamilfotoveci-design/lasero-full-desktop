using System.Windows;
using System.Windows.Controls;
using Lasero.App.Controls;
using Lasero.Core.Scene;

namespace Lasero.App.Views;

/// <summary>
/// Node Edit mode's contextual bar — Corner/Smooth, open/close path, delete node. Mirrors
/// CanvasViewControls' own TargetCanvas precedent: this drives SceneCanvas's own public node-edit
/// entry points directly, since the state it reflects (which nodes are selected, their type) is view
/// state the canvas owns, with no SceneViewModel equivalent to bind against instead.
/// </summary>
public partial class NodeEditToolbar : UserControl
{
    public static readonly DependencyProperty TargetCanvasProperty = DependencyProperty.Register(
        nameof(TargetCanvas), typeof(SceneCanvas), typeof(NodeEditToolbar), new PropertyMetadata(null));

    public SceneCanvas? TargetCanvas
    {
        get => (SceneCanvas?)GetValue(TargetCanvasProperty);
        set => SetValue(TargetCanvasProperty, value);
    }

    public NodeEditToolbar()
    {
        InitializeComponent();
    }

    private void OnCornerClick(object sender, RoutedEventArgs e) => TargetCanvas?.ConvertSelectedNodes(VectorNodeType.Corner);

    private void OnSmoothClick(object sender, RoutedEventArgs e) => TargetCanvas?.ConvertSelectedNodes(VectorNodeType.Smooth);

    private void OnToggleClosedClick(object sender, RoutedEventArgs e) => TargetCanvas?.ToggleSelectedSubpathClosed();

    private void OnDeleteNodesClick(object sender, RoutedEventArgs e) => TargetCanvas?.DeleteSelectedNodes();

    private void OnBreakAtNodeClick(object sender, RoutedEventArgs e) => TargetCanvas?.BreakSelectedNode();

    /// <summary>One button, two actions — mirrors OnToggleClosedClick's own "swap by current state"
    /// convention: converts the hovered segment to a curve if it is currently straight, or back to a
    /// line if it is currently curved (IsHoveredSegmentStraight null means no segment is hovered, the
    /// same state the button's own Visibility binding already hides it for).</summary>
    private void OnConvertSegmentClick(object sender, RoutedEventArgs e)
    {
        if (TargetCanvas?.IsHoveredSegmentStraight is not { } isStraight) return;
        if (isStraight) TargetCanvas.ConvertHoveredSegmentToCurve();
        else TargetCanvas.ConvertHoveredSegmentToLine();
    }

    private void OnInsertMidpointClick(object sender, RoutedEventArgs e) => TargetCanvas?.InsertNodeAtHoveredSegmentMidpoint();

    private void OnDeleteSegmentClick(object sender, RoutedEventArgs e) => TargetCanvas?.DeleteHoveredSegment();

    private void OnSelectAllNodesClick(object sender, RoutedEventArgs e) => TargetCanvas?.SelectAllNodes();

    private void OnClearNodeSelectionClick(object sender, RoutedEventArgs e) => TargetCanvas?.ClearNodeSelection();

    private void OnReversePathClick(object sender, RoutedEventArgs e) => TargetCanvas?.ReverseSelectedSubpath();

    private void OnJoinClick(object sender, RoutedEventArgs e) => TargetCanvas?.JoinSelectedEndpointToCandidate();
}
