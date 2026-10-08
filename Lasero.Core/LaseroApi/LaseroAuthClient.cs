using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Lasero.Core.LaseroApi;

public sealed class LaseroAuthClient
{
    private const string FirebaseApiKey = "AIzaSyCpZ7uvUCoA1xjqo60Z8qcGDeDqJ1zihaI";
    private const string SignInUrl = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={FirebaseApiKey}";
    private const string ResetPasswordUrl = $"https://identitytoolkit.googleapis.com/v1/accounts:sendOobCode?key={FirebaseApiKey}";
    private const string RefreshUrl = $"https://securetoken.googleapis.com/v1/token?key={FirebaseApiKey}";

    private readonly HttpClient _http;

    public LaseroAuthClient(HttpClient http) => _http = http;

    public async Task<FirebaseSession> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(SignInUrl,
            new { email, password, returnSecureToken = true }, ct);

        if (!response.IsSuccessStatusCode)
            throw await CreateAuthExceptionAsync(response, ct);

        var result = await response.Content.ReadFromJsonAsync<SignInResponse>(cancellationToken: ct)
            ?? throw new LaseroAuthException("Server nevrátil žádná data.");

        return ToSession(result.IdToken, result.RefreshToken, result.LocalId, result.Email, result.ExpiresIn);
    }

    public async Task SendPasswordResetAsync(string email, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(ResetPasswordUrl,
            new { requestType = "PASSWORD_RESET", email }, ct);

        if (!response.IsSuccessStatusCode)
            throw await CreateAuthExceptionAsync(response, ct);
    }

    public async Task<FirebaseSession> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        });

        var response = await _http.PostAsync(RefreshUrl, form, ct);
        if (!response.IsSuccessStatusCode)
            throw new LaseroAuthException("Obnovení přihlášení se nezdařilo. Je potřeba se přihlásit znovu.");

        var result = await response.Content.ReadFromJsonAsync<RefreshResponse>(cancellationToken: ct)
            ?? throw new LaseroAuthException("Server nevrátil žádná data.");

        return ToSession(result.IdToken, result.RefreshToken, result.UserId, null, result.ExpiresIn);
    }

    private static async Task<LaseroAuthException> CreateAuthExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var error = await response.Content.ReadFromJsonAsync<FirebaseErrorEnvelope>(cancellationToken: ct);
        return new LaseroAuthException(TranslateError(error?.Error?.Message));
    }

    private static FirebaseSession ToSession(string idToken, string refreshToken, string localId, string? email, string expiresInSeconds) =>
        new()
        {
            IdToken = idToken,
            RefreshToken = refreshToken,
            LocalId = localId,
            Email = email,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(double.Parse(expiresInSeconds, CultureInfo.InvariantCulture)),
        };

    private static string TranslateError(string? firebaseCode) => firebaseCode switch
    {
        "EMAIL_NOT_FOUND" => "Účet s tímto e-mailem nebyl nalezen.",
        "INVALID_PASSWORD" or "INVALID_LOGIN_CREDENTIALS" => "Nesprávný e-mail nebo heslo.",
        "INVALID_EMAIL" => "E-mail není platný.",
        "MISSING_PASSWORD" => "Heslo chybí.",
        "USER_DISABLED" => "Tento účet byl deaktivován.",
        "TOO_MANY_ATTEMPTS_TRY_LATER" => "Příliš mnoho pokusů. Lze to zkusit za chvíli.",
        "WEAK_PASSWORD" => "Heslo je příliš slabé.",
        null => "Přihlášení se nezdařilo.",
        _ => $"Přihlášení se nezdařilo ({firebaseCode}).",
    };

    private sealed record SignInResponse(
        [property: JsonPropertyName("idToken")] string IdToken,
        [property: JsonPropertyName("refreshToken")] string RefreshToken,
        [property: JsonPropertyName("localId")] string LocalId,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("expiresIn")] string ExpiresIn);

    private sealed record RefreshResponse(
        [property: JsonPropertyName("id_token")] string IdToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("expires_in")] string ExpiresIn);

    private sealed record FirebaseErrorEnvelope([property: JsonPropertyName("error")] FirebaseError? Error);
    private sealed record FirebaseError([property: JsonPropertyName("message")] string? Message);
}

public sealed class LaseroAuthException(string message) : Exception(message);
