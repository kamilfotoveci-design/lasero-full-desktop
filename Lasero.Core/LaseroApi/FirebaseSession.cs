namespace Lasero.Core.LaseroApi;

/// <summary>A signed-in Firebase session: enough to call lasero.net's Bearer-token-authenticated functions and to refresh silently later.</summary>
public sealed record FirebaseSession
{
    public required string IdToken { get; init; }
    public required string RefreshToken { get; init; }
    public required string LocalId { get; init; }
    public string? Email { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromMinutes(2);
}
