namespace Lasero.Core.History;

/// <summary>One entry in the Home dashboard's recent-projects list. ThumbnailPath points at a cached
/// PNG rendered from the project's own scene geometry (see SceneThumbnailRenderer) — null until the
/// first render succeeds.</summary>
public sealed record RecentProjectEntry(string Path, string Name, DateTime LastOpenedUtc, string? ThumbnailPath);
