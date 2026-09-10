using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Lasero.App.ViewModels;
using Lasero.Core.LaseroApi;

namespace Lasero.Tests;

/// <summary>Proves the passive device-activation call (Phase 2, goal 3) is wired correctly at the
/// AccountViewModel level: fires with the freshly-signed-in account's own id token, never fires on
/// sign-out, and — critically — never affects sign-in success even when the activation call itself
/// fails. This is telemetry only; these tests exist to prove it can never become a gate.</summary>
public sealed class AccountDeviceActivationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-device-activation-tests", Guid.NewGuid().ToString("N"));

    public AccountDeviceActivationTests() => Directory.CreateDirectory(_directory);

    private AccountViewModel CreateAccount(RecordingHandler deviceActivationHandler, string localId = "account-a", string idToken = "test-id-token")
    {
        var authHandler = new StubHandler(_ => Json(HttpStatusCode.OK,
            $$"""{"idToken":"{{idToken}}","refreshToken":"test-refresh-token","localId":"{{localId}}","email":"user@example.com","expiresIn":"3600"}"""));
        var accountHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        return new AccountViewModel(
            new LaseroAuthClient(new HttpClient(authHandler)),
            new LaseroAccountClient(new HttpClient(accountHandler)),
            new SessionStore(Path.Combine(_directory, $"session-{Guid.NewGuid():N}.dat")),
            new DeviceIdStore(Path.Combine(_directory, "device-id.txt")),
            new DeviceActivationClient(new HttpClient(deviceActivationHandler)));
    }

    private static async Task SignIn(AccountViewModel account, string email = "user@example.com", string password = "password123")
    {
        account.Email = email;
        account.Password = password;
        await account.SignInCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task SignIn_RegistersDeviceActivationWithTheSignedInAccountsIdToken()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var account = CreateAccount(handler);

        await SignIn(account);
        var request = await handler.WaitForRequestAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal("test-id-token", request.AuthorizationParameter);
        Assert.Contains("\"platform\":\"desktop\"", request.Body);
        Assert.Contains("\"deviceId\"", request.Body);
    }

    [Fact]
    public async Task SignIn_UsesAStableDeviceIdAcrossCalls()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var deviceIdStore = new DeviceIdStore(Path.Combine(_directory, "device-id.txt"));
        var expectedDeviceId = deviceIdStore.GetOrCreate();

        var account = CreateAccount(handler);
        await SignIn(account);
        var request = await handler.WaitForRequestAsync(TimeSpan.FromSeconds(5));

        Assert.Contains($"\"deviceId\":\"{expectedDeviceId}\"", request.Body);
    }

    [Fact]
    public async Task DifferentAccounts_EachRegisterWithTheirOwnIdToken()
    {
        var handlerA = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var accountA = CreateAccount(handlerA, localId: "account-a", idToken: "token-a");
        await SignIn(accountA);
        var requestA = await handlerA.WaitForRequestAsync(TimeSpan.FromSeconds(5));

        var handlerB = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var accountB = CreateAccount(handlerB, localId: "account-b", idToken: "token-b");
        await SignIn(accountB);
        var requestB = await handlerB.WaitForRequestAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("token-a", requestA.AuthorizationParameter);
        Assert.Equal("token-b", requestB.AuthorizationParameter);
    }

    [Fact]
    public async Task SignOut_DoesNotTriggerAnActivationCall()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var account = CreateAccount(handler);
        await SignIn(account);
        await handler.WaitForRequestAsync(TimeSpan.FromSeconds(5));
        Assert.Single(handler.Requests);

        account.SignOutCommand.Execute(null);

        // Bounded wait for a call that should never come — these are local in-process fake
        // handlers with no real network latency, so a call that was going to fire would do so
        // almost immediately; this window is generous without making the suite noticeably slower.
        var sawUnexpectedCall = await handler.TryWaitForAnotherRequestAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(sawUnexpectedCall);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ActivationCallFailure_DoesNotFailSignIn()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var account = CreateAccount(handler);

        await SignIn(account);
        await handler.WaitForRequestAsync(TimeSpan.FromSeconds(5));

        Assert.True(account.IsSignedIn);
    }

    [Fact]
    public async Task ActivationCallThrowing_DoesNotFailSignIn()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("simulated network failure"));
        var account = CreateAccount(handler);

        await SignIn(account);
        await handler.WaitForRequestAsync(TimeSpan.FromSeconds(5));

        Assert.True(account.IsSignedIn);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed record CapturedRequest(string? AuthorizationScheme, string? AuthorizationParameter, string Body);

    /// <summary>Captures every request it receives — reading headers and body eagerly, inside
    /// SendAsync, since the real HttpClient disposes the request (and its content) shortly after
    /// this handler returns — and lets a test await the next one deterministically via a semaphore
    /// instead of polling or sleeping arbitrarily.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        private readonly SemaphoreSlim _signal = new(0);
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var captured = new CapturedRequest(request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter, body);
            lock (Requests) Requests.Add(captured);
            _signal.Release();
            return response(request);
        }

        public async Task<CapturedRequest> WaitForRequestAsync(TimeSpan timeout)
        {
            if (!await _signal.WaitAsync(timeout))
                throw new TimeoutException("No HTTP request was captured within the timeout.");
            lock (Requests) return Requests[^1];
        }

        public async Task<bool> TryWaitForAnotherRequestAsync(TimeSpan timeout) => await _signal.WaitAsync(timeout);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
