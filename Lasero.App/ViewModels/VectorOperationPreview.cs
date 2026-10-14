using Lasero.Core.Scene;

namespace Lasero.App.ViewModels;

/// <summary>Prepared Boolean edit. Nothing enters the document until Commit is called.</summary>
public sealed record VectorOperationPreview(
    string Title,
    IReadOnlyList<SceneObject> Sources,
    IReadOnlyList<SceneObject> Results,
    string ResultLayer,
    bool ApproximatesCurves,
    Action Commit,
    string? GeometryNotice = null,
    string? LayerNotice = null);
