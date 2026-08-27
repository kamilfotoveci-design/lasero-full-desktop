using System.Text.Json.Serialization;

namespace Lasero.Core.LaseroApi;

/// <summary>Mirrors check-premium.js's response shape exactly.</summary>
public sealed record PremiumStatus(
    [property: JsonPropertyName("isPremium")] bool IsPremium,
    [property: JsonPropertyName("trialDaysLeft")] int TrialDaysLeft,
    [property: JsonPropertyName("licenseExpiry")] long? LicenseExpiry,
    [property: JsonPropertyName("playExpiry")] long? PlayExpiry,
    [property: JsonPropertyName("appleExpiry")] long? AppleExpiry);
