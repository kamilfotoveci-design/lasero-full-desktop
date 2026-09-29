using System.Drawing;
using System.IO;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Lasero.Core.BackgroundRemoval;

namespace Lasero.Tests;

/// <summary>Exercises the cloud background-removal client end to end against a fake
/// HttpMessageHandler. No test here talks to the real Lasero proxy or to Gemini.</summary>
public sealed class GeminiBackgroundRemovalServiceTests : IDisposable
{
    private const string SecretToken = "secret-id-token-value";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-bgr-" + Guid.NewGuid().ToString("N"));

    public GeminiBackgroundRemovalServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Success_AppliesMaskToOriginalPixelsAndSendsBearerToken()
    {
        var source = WriteSourcePng();
        HttpRequestMessage? seen = null;
        var service = CreateService(request =>
        {
            seen = request;
            return Task.FromResult(MaskResponse(MaskPng()));
        });
        var destination = Path.Combine(_directory, "out.nobg.png");

        var result = await service.RemoveBackgroundAsync(source, destination);

        Assert.Equal(destination, result);
        Assert.NotNull(seen);
        Assert.Equal(GeminiBackgroundRemovalService.ProxyEndpoint, seen!.RequestUri!.ToString());
        Assert.Equal("Bearer", seen.Headers.Authorization!.Scheme);
        Assert.Equal(SecretToken, seen.Headers.Authorization.Parameter);
        using var output = new Bitmap(destination);
        Assert.Equal(64, output.Width);
        Assert.Equal(64, output.Height);
        Assert.Equal(0, output.GetPixel(2, 2).A);
        var center = output.GetPixel(32, 32);
        Assert.Equal(255, center.A);
        Assert.Equal(Color.FromArgb(220, 30, 30).ToArgb() & 0xFFFFFF, center.ToArgb() & 0xFFFFFF);
        // The source file must be untouched.
        using var original = new Bitmap(source);
        Assert.Equal(255, original.GetPixel(2, 2).A);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, BackgroundRemovalFailure.SignInRequired)]
    [InlineData(HttpStatusCode.Forbidden, BackgroundRemovalFailure.AccessDenied)]
    [InlineData(HttpStatusCode.TooManyRequests, BackgroundRemovalFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, BackgroundRemovalFailure.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway, BackgroundRemovalFailure.ServiceUnavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, BackgroundRemovalFailure.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, BackgroundRemovalFailure.TooLarge)]
    [InlineData(HttpStatusCode.BadRequest, BackgroundRemovalFailure.InvalidImage)]
    [InlineData(HttpStatusCode.NotFound, BackgroundRemovalFailure.NotConfigured)]
    [InlineData(HttpStatusCode.RequestTimeout, BackgroundRemovalFailure.Timeout)]
    [InlineData(HttpStatusCode.Conflict, BackgroundRemovalFailure.Unknown)]
    public async Task HttpStatus_IsMappedToShortCzechMessage(HttpStatusCode status, BackgroundRemovalFailure expected)
    {
        var service = CreateService(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("provider internals " + SecretToken),
        }));

        var exception = await Assert.ThrowsAsync<BackgroundRemovalException>(
            () => service.RemoveBackgroundAsync(WriteSourcePng(), Path.Combine(_directory, "x.png")));

        Assert.Equal(expected, exception.Failure);
        AssertUserMessage(exception.Message);
        Assert.DoesNotContain(SecretToken, exception.Message);
        Assert.DoesNotContain("provider internals", exception.Message);
        Assert.False(File.Exists(Path.Combine(_directory, "x.png")));
    }

    [Fact]
    public async Task NetworkFailure_IsMappedToNetwork()
    {
        var service = CreateService(_ => throw new HttpRequestException("dns failure"));

        var exception = await Assert.ThrowsAsync<BackgroundRemovalException>(
            () => service.RemoveBackgroundAsync(WriteSourcePng()));

        Assert.Equal(BackgroundRemovalFailure.Network, exception.Failure);
        AssertUserMessage(exception.Message);
    }

    [Fact]
    public async Task SlowService_TimesOutWithoutBeingTreatedAsUserCancellation()
    {
        var service = CreateService(async request =>
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, timeout: TimeSpan.FromMilliseconds(150), honorCancellation: true);

        var exception = await Assert.ThrowsAsync<BackgroundRemovalException>(
            () => service.RemoveBackgroundAsync(WriteSourcePng()));

        Assert.Equal(BackgroundRemovalFailure.Timeout, exception.Failure);
        AssertUserMessage(exception.Message);
    }

    [Fact]
    public async Task UserCancellation_PropagatesAsOperationCanceledAndWritesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        var service = CreateService(async request =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, honorCancellation: true);
        var destination = Path.Combine(_directory, "cancelled.png");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RemoveBackgroundAsync(WriteSourcePng(), destination, cancellation.Token));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task AlreadyCancelledToken_NeverContactsTheService()
    {
        var calls = 0;
        var service = CreateService(_ => { calls++; return Task.FromResult(MaskResponse(MaskPng())); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RemoveBackgroundAsync(WriteSourcePng(), cancellationToken: cancellation.Token));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MissingSignIn_IsReportedBeforeAnyRequest()
    {
        var calls = 0;
        var service = new GeminiBackgroundRemovalService(
            new HttpClient(new StubHandler(_ => { calls++; return Task.FromResult(MaskResponse(MaskPng())); })),
            _ => throw new InvalidOperationException("Online přihlášení není dostupné."));

        var exception = await Assert.ThrowsAsync<BackgroundRemovalException>(
            () => service.RemoveBackgroundAsync(WriteSourcePng()));

        Assert.Equal(BackgroundRemovalFailure.SignInRequired, exception.Failure);
        AssertUserMessage(exception.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OversizedFile_IsRejectedLocallyWithoutUpload()
    {
        var calls = 0;
        var service = CreateService(_ => { calls++; return Task.FromResult(MaskResponse(MaskPng())); });
        var path = Path.Combine(_directory, "huge.png");
        File.WriteAllBytes(path, [.. File.ReadAllBytes(WriteSourcePng()), .. new byte[4 * 1024 * 1024 + 16]]);

        var exception = await Assert.ThrowsAsync<BackgroundRemovalException>(() => service.RemoveBackgroundAsync(path));

        Assert.Equal(BackgroundRemovalFailure.TooLarge, exception.Failure);
        AssertUserMessage(exception.Message);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("notes.png", "this is not an image")]
    [InlineData("anim.gif", "GIF89a")]
    public async Task UnreadableOrUnsupportedSource_IsInvalidImage(string name, string content)
    {
        var calls = 0;
        var service = CreateService(_ => { calls++; return Task.FromResult(MaskResponse(MaskPng())); });
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);

        var exception = await Assert.ThrowsAsync<BackgroundRemovalException>(() => service.RemoveBackgroundAsync(path));

        Assert.Equal(BackgroundRemovalFailure.InvalidImage, exception.Failure);
        AssertUserMessage(exception.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MissingSourceFile_IsInvalidImage()
    {
        var service = CreateService(_ => Task.FromResult(MaskResponse(MaskPng())));

        var exception = await Assert.ThrowsAsync<BackgroundRemovalException>(
            () => service.RemoveBackgroundAsync(Path.Combine(_directory, "gone.png")));

        Assert.Equal(BackgroundRemovalFailure.InvalidImage, exception.Failure);
        AssertUserMessage(exception.Message);
    }

    public static TheoryData<string> BadBodies() => new()
    {
        "not json at all",
        "{}",
        "{\"error\":{\"message\":\"blocked by safety\"}}",
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"no image\"}]}}]}",
        "{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"image/gif\",\"data\":\"AAAA\"}}]}}]}",
        "{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\"@@not-base64@@\"}}]}}]}",
        "{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"image/png\",\"data\":\"AAAAAAAA\"}}]}}]}",
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task UnusableResponse_IsInvalidResultAndLeavesNoFile(string body)
    {
        var service = CreateService(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));
        var destination = Path.Combine(_directory, "never.png");

        var exception = await Assert.ThrowsAsync<BackgroundRemovalException>(
            () => service.RemoveBackgroundAsync(WriteSourcePng(), destination));

        Assert.Equal(BackgroundRemovalFailure.InvalidResult, exception.Failure);
        AssertUserMessage(exception.Message);
        Assert.DoesNotContain("blocked by safety", exception.Message);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task FlatGrayMask_IsRejectedAsUnusable()
    {
        using var gray = new Bitmap(64, 64);
        using (var graphics = Graphics.FromImage(gray)) graphics.Clear(Color.Gray);
        var service = CreateService(_ => Task.FromResult(MaskResponse(ToPng(gray))));

        var exception = await Assert.ThrowsAsync<BackgroundRemovalException>(
            () => service.RemoveBackgroundAsync(WriteSourcePng(), Path.Combine(_directory, "gray.png")));

        Assert.Equal(BackgroundRemovalFailure.InvalidResult, exception.Failure);
    }

    [Fact]
    public async Task MaskWithChangedAspectRatio_IsRejected()
    {
        using var wide = new Bitmap(128, 64);
        using (var graphics = Graphics.FromImage(wide))
        {
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.White, 40, 16, 48, 32);
        }
        var service = CreateService(_ => Task.FromResult(MaskResponse(ToPng(wide))));

        var exception = await Assert.ThrowsAsync<BackgroundRemovalException>(
            () => service.RemoveBackgroundAsync(WriteSourcePng(), Path.Combine(_directory, "wide.png")));

        Assert.Equal(BackgroundRemovalFailure.InvalidResult, exception.Failure);
    }

    // ---- fallback -------------------------------------------------------------------------

    [Fact]
    public async Task Fallback_UsesLocalServiceWhenCloudIsNotConfigured()
    {
        var cloud = new FakeService(_ => throw new BackgroundRemovalException(BackgroundRemovalFailure.SignInRequired, "Přihlaste se."));
        var local = new FakeService(_ => "local.nobg.png") { Ready = true };
        var service = new FallbackBackgroundRemovalService(cloud, local);

        var path = await service.RemoveBackgroundAsync("photo.png");

        Assert.Equal("local.nobg.png", path);
        Assert.True(service.LastRunWasLocal);
        Assert.Equal(1, cloud.Calls);
        Assert.Equal(1, local.Calls);
    }

    [Theory]
    [InlineData(BackgroundRemovalFailure.Network)]
    [InlineData(BackgroundRemovalFailure.ServiceUnavailable)]
    [InlineData(BackgroundRemovalFailure.RateLimited)]
    [InlineData(BackgroundRemovalFailure.TooLarge)]
    public async Task Fallback_UsesLocalServiceForTransientCloudFailures(BackgroundRemovalFailure failure)
    {
        var cloud = new FakeService(_ => throw new BackgroundRemovalException(failure, "x"));
        var local = new FakeService(_ => "local.nobg.png") { Ready = true };

        var path = await new FallbackBackgroundRemovalService(cloud, local).RemoveBackgroundAsync("photo.png");

        Assert.Equal("local.nobg.png", path);
    }

    [Fact]
    public async Task Fallback_SurfacesCloudMessageWhenLocalModelIsNotReady()
    {
        var cloud = new FakeService(_ => throw new BackgroundRemovalException(BackgroundRemovalFailure.SignInRequired, "Přihlaste se."));
        var local = new FakeService(_ => "local.nobg.png") { Ready = false };

        var exception = await Assert.ThrowsAsync<BackgroundRemovalException>(
            () => new FallbackBackgroundRemovalService(cloud, local).RemoveBackgroundAsync("photo.png"));

        Assert.Equal(BackgroundRemovalFailure.SignInRequired, exception.Failure);
        Assert.Equal("Přihlaste se.", exception.Message);
        Assert.Equal(0, local.Calls);
    }

    [Fact]
    public async Task Fallback_DoesNotRetryLocallyForAnInvalidImage()
    {
        var cloud = new FakeService(_ => throw new BackgroundRemovalException(BackgroundRemovalFailure.InvalidImage, "Špatný soubor."));
        var local = new FakeService(_ => "local.nobg.png") { Ready = true };

        var exception = await Assert.ThrowsAsync<BackgroundRemovalException>(
            () => new FallbackBackgroundRemovalService(cloud, local).RemoveBackgroundAsync("photo.png"));

        Assert.Equal(BackgroundRemovalFailure.InvalidImage, exception.Failure);
        Assert.Equal(0, local.Calls);
    }

    [Fact]
    public async Task Fallback_ReportsLocalFailureTogetherWithCloudReason()
    {
        var cloud = new FakeService(_ => throw new BackgroundRemovalException(BackgroundRemovalFailure.Network, "Bez připojení."));
        var local = new FakeService(_ => throw new InvalidOperationException("onnx exploded")) { Ready = true };

        var exception = await Assert.ThrowsAsync<BackgroundRemovalException>(
            () => new FallbackBackgroundRemovalService(cloud, local).RemoveBackgroundAsync("photo.png"));

        Assert.Equal(BackgroundRemovalFailure.LocalFailed, exception.Failure);
        Assert.Contains("Bez připojení.", exception.Message);
        Assert.DoesNotContain("onnx exploded", exception.Message);
    }

    [Fact]
    public async Task Fallback_PassesUserCancellationThrough()
    {
        var cloud = new FakeService(_ => throw new OperationCanceledException());
        var local = new FakeService(_ => "local.nobg.png") { Ready = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FallbackBackgroundRemovalService(cloud, local).RemoveBackgroundAsync("photo.png"));

        Assert.Equal(0, local.Calls);
    }

    [Fact]
    public async Task Fallback_PrefersCloudWhenItSucceeds()
    {
        var cloud = new FakeService(_ => "cloud.nobg.png");
        var local = new FakeService(_ => "local.nobg.png") { Ready = true };
        var service = new FallbackBackgroundRemovalService(cloud, local);

        Assert.Equal("cloud.nobg.png", await service.RemoveBackgroundAsync("photo.png"));
        Assert.False(service.LastRunWasLocal);
        Assert.Equal(0, local.Calls);
    }

    // ---- helpers --------------------------------------------------------------------------

    private static void AssertUserMessage(string message)
    {
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain('?', message);
        Assert.DoesNotContain('!', message);
        Assert.True(message.Length <= 160, "message should stay short: " + message);
    }

    private GeminiBackgroundRemovalService CreateService(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond,
        TimeSpan? timeout = null,
        bool honorCancellation = false) =>
        new(new HttpClient(new StubHandler(respond, honorCancellation)),
            _ => Task.FromResult(SecretToken), timeout);

    private string WriteSourcePng()
    {
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(40, 80, 200));
            using var brush = new SolidBrush(Color.FromArgb(220, 30, 30));
            graphics.FillRectangle(brush, 16, 16, 32, 32);
        }
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static byte[] MaskPng()
    {
        using var mask = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(mask))
        {
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.White, 16, 16, 32, 32);
        }
        return ToPng(mask);
    }

    private static byte[] ToPng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static HttpResponseMessage MaskResponse(byte[] png)
    {
        var body = JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new { content = new { parts = new object[] { new { inlineData = new { mimeType = "image/png", data = Convert.ToBase64String(png) } } } } },
            },
        });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, bool honorCancellation = false)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!honorCancellation) return await respond(request);
            var work = respond(request);
            var cancelled = new TaskCompletionSource();
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            if (await Task.WhenAny(work, cancelled.Task) == cancelled.Task)
                throw new TaskCanceledException();
            return await work;
        }
    }

    private sealed class FakeService(Func<string, string> run) : IBackgroundRemovalService
    {
        public bool Ready { get; set; } = true;
        public int Calls { get; private set; }
        public bool IsReady => Ready;
        public Task PrepareAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> RemoveBackgroundAsync(string sourceFilePath, string? destinationFilePath = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(run(sourceFilePath));
        }
    }
}
