namespace Lasero.Core.History;

/// <summary>One completed engraving/cutting run — appended when a real job finishes (never a framing pass).
/// MaterialName is free-text and optional since the app has no material library yet.</summary>
public sealed record JobHistoryEntry(string Name, string? MaterialName, double DurationSeconds, DateTime CompletedUtc);
