using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lasero.Core.BackgroundRemoval;

/// <summary>
/// Sends an explicitly requested raster edit through Lasero's authenticated Gemini proxy. The
/// desktop binary contains no Gemini key: the request carries the signed-in user's ID token, which
/// is never logged. Gemini returns a grayscale foreground mask; Lasero applies that mask to the
/// original pixels so the model cannot redraw or retouch the engraved subject.
/// Every failure is a <see cref="BackgroundRemovalException"/> with a short Czech message.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GeminiBackgroundRemovalService : IBackgroundRemovalService
{
    public const string ProxyEndpoint = "https://lasero.net/.netlify/functions/gemini";
    public const string Model = "gemini-3.1-flash-image";
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(55);
    // Netlify buffered functions cap both request and response bodies at 6 MB. Base64 expands
    // image bytes by about one third, so retain headroom for JSON and proxy metadata.
    private const int MaximumInputBytes = 4 * 1024 * 1024;
    private const int MaximumOutputBytes = 5 * 1024 * 1024;
    private const long MaximumPixelCount = 16_000_000;

    private const string Prompt = "Create a precise foreground segmentation mask for the supplied image. Return only a flat grayscale mask with a pure black (#000000) background and the complete main foreground subject in pure white (#FFFFFF). Keep the original framing, orientation, aspect ratio, position, and outline exactly aligned. No original-image texture, colors, shadows, gradients, checkerboard, labels, or extra content. Use only a narrow antialiased gray transition on the subject boundary. The mask must be black outside the foreground and white inside it.";

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _getIdToken;
    private readonly TimeSpan _requestTimeout;

    public GeminiBackgroundRemovalService(
        HttpClient http, Func<CancellationToken, Task<string>> getIdToken, TimeSpan? requestTimeout = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _getIdToken = getIdToken ?? throw new ArgumentNullException(nameof(getIdToken));
        _requestTimeout = requestTimeout is { } value && value > TimeSpan.Zero ? value : DefaultRequestTimeout;
    }

    public bool IsReady => true;

    public Task PrepareAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<string> RemoveBackgroundAsync(
        string sourceFilePath, string? destinationFilePath = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        cancellationToken.ThrowIfCancellationRequested();

        var (sourceBytes, sourceExtension, sourceWidth, sourceHeight) = ReadSource(sourceFilePath);
        var (uploadBytes, mimeType) = NormalizeUploadChecked(sourceBytes, sourceExtension);
        cancellationToken.ThrowIfCancellationRequested();
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);

        byte[] payload;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_requestTimeout);
            try
            {
                payload = await SendAsync(token, mimeType, uploadBytes, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Our own deadline or HttpClient.Timeout, not the caller's cancellation.
                throw new BackgroundRemovalException(BackgroundRemovalFailure.Timeout,
                    "Služba neodpověděla včas. Zkuste to znovu.");
            }
            catch (HttpRequestException exception)
            {
                throw new BackgroundRemovalException(BackgroundRemovalFailure.Network,
                    "Nelze se připojit ke službě. Zkontrolujte připojení k internetu.", exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var (maskBytes, maskMimeType) = ReadImagePart(payload);
            cancellationToken.ThrowIfCancellationRequested();

            var outputPath = destinationFilePath ?? GetDestinationPath(sourceBytes);
            await WriteMaskedPngAsync(maskBytes, maskMimeType, sourceBytes, sourceExtension,
                outputPath, sourceWidth, sourceHeight, cancellationToken).ConfigureAwait(false);
            return outputPath;
        }
        catch (JsonException exception)
        {
            throw Result("Služba vrátila nečitelnou odpověď. Původní obrázek zůstal beze změny.", exception);
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException or OutOfMemoryException)
        {
            throw Result("Výsledek služby nelze zpracovat. Původní obrázek zůstal beze změny.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BackgroundRemovalException(BackgroundRemovalFailure.Unknown,
                "Výsledek se nepodařilo uložit na disk. Zkontrolujte volné místo.", exception);
        }
    }

    private static (byte[] Bytes, string Extension, int Width, int Height) ReadSource(string sourceFilePath)
    {
        const string TooLargeMessage = "Soubor je příliš velký pro cloudové odstranění pozadí (limit 4 MB). Zkuste menší obrázek.";
        try
        {
            var sourcePath = Path.GetFullPath(sourceFilePath);
            var extension = Path.GetExtension(sourcePath);
            if (new FileInfo(sourcePath).Length > MaximumInputBytes)
                throw new BackgroundRemovalException(BackgroundRemovalFailure.TooLarge, TooLargeMessage);
            var bytes = File.ReadAllBytes(sourcePath);
            var (width, height) = ReadDimensions(bytes, extension);
            if ((long)width * height > MaximumPixelCount)
                throw new BackgroundRemovalException(BackgroundRemovalFailure.TooLarge,
                    "Obrázek je příliš velký pro cloudové odstranění pozadí. Zkuste menší rozlišení.");
            if (bytes.Length > MaximumInputBytes)
                throw new BackgroundRemovalException(BackgroundRemovalFailure.TooLarge, TooLargeMessage);
            return (bytes, extension, width, height);
        }
        catch (InvalidDataException exception)
        {
            throw new BackgroundRemovalException(BackgroundRemovalFailure.InvalidImage, exception.Message, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new BackgroundRemovalException(BackgroundRemovalFailure.InvalidImage,
                "Soubor obrázku se nepodařilo načíst. Zkontrolujte, zda stále existuje.", exception);
        }
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await _getIdToken(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            throw new BackgroundRemovalException(BackgroundRemovalFailure.SignInRequired,
                "Pro odstranění pozadí se přihlaste k účtu Lasero.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new BackgroundRemovalException(BackgroundRemovalFailure.Network,
                "Přihlášení nelze ověřit bez připojení k internetu.", exception);
        }
        catch (Exception exception)
        {
            throw new BackgroundRemovalException(BackgroundRemovalFailure.SignInRequired,
                "Přihlášení se nepodařilo obnovit. Přihlaste se prosím znovu.", exception);
        }

        if (string.IsNullOrWhiteSpace(token))
            throw new BackgroundRemovalException(BackgroundRemovalFailure.SignInRequired,
                "Pro odstranění pozadí se přihlaste k účtu Lasero.");
        return token;
    }

    private async Task<byte[]> SendAsync(string token, string mimeType, byte[] uploadBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ProxyEndpoint)
        {
            Content = JsonContent.Create(new
            {
                model = Model,
                body = new
                {
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new object[]
                            {
                                new { text = Prompt },
                                new { inline_data = new { mime_type = mimeType, data = Convert.ToBase64String(uploadBytes) } },
                            },
                        },
                    },
                    // Ask for an image only; Gemini's image-edit flow otherwise matches the
                    // source image's aspect ratio by default. Avoid forcing a preset ratio here.
                    generationConfig = new { responseModalities = new[] { "IMAGE" } },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw MapStatus(response.StatusCode);
        return await ReadBoundedAsync(response.Content, MaximumOutputBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps an HTTP status to a short Czech message. The response body is deliberately not
    /// read or shown: it may contain provider internals.</summary>
    internal static BackgroundRemovalException MapStatus(HttpStatusCode status) => (int)status switch
    {
        401 => new(BackgroundRemovalFailure.SignInRequired, "Přihlášení vypršelo. Přihlaste se prosím znovu."),
        403 => new(BackgroundRemovalFailure.AccessDenied, "Váš účet nemá k odstranění pozadí přístup."),
        408 => new(BackgroundRemovalFailure.Timeout, "Služba neodpověděla včas. Zkuste to znovu."),
        413 => new(BackgroundRemovalFailure.TooLarge, "Obrázek je pro službu příliš velký. Zkuste menší obrázek."),
        400 or 415 or 422 => new(BackgroundRemovalFailure.InvalidImage, "Služba obrázek nepřijala. Zkuste jiný soubor."),
        404 or 501 => new(BackgroundRemovalFailure.NotConfigured, "Odstranění pozadí není na serveru nastavené."),
        429 => new(BackgroundRemovalFailure.RateLimited, "Služba je nyní vytížená nebo byl dosažen limit požadavků. Zkuste to později."),
        >= 500 => new(BackgroundRemovalFailure.ServiceUnavailable, "Služba je dočasně nedostupná. Zkuste to za chvíli."),
        _ => new(BackgroundRemovalFailure.Unknown, "Odstranění pozadí se nezdařilo. Zkuste to znovu."),
    };

    private static BackgroundRemovalException Result(string message, Exception? inner = null) =>
        new(BackgroundRemovalFailure.InvalidResult, message, inner);

    private static (byte[] Bytes, string MimeType) NormalizeUpload(byte[] bytes, string extension)
    {
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) return (bytes, "image/png");
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            return (bytes, "image/jpeg");

        using var input = new MemoryStream(bytes, writable: false);
        using var bitmap = new Bitmap(input);
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        if (output.Length > MaximumInputBytes)
            throw new BackgroundRemovalException(BackgroundRemovalFailure.TooLarge, "Převedený obrázek je příliš velký pro cloudové odstranění pozadí (limit 4 MB). Zkuste menší obrázek.");
        return (output.ToArray(), "image/png");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
            throw Result("Odpověď služby překročila povolenou velikost.");

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw Result("Odpověď služby překročila povolenou velikost.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static (byte[] Bytes, string MimeType) ReadImagePart(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        // The provider's own error text is never shown: it is untranslated and may leak internals.
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
            throw Result("Služba odmítla požadavek. Původní obrázek zůstal beze změny.");

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            throw Result("Služba nevrátila žádný výsledek. Původní obrázek zůstal beze změny.");

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
                continue;
            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("inlineData", out var inlineData) && !part.TryGetProperty("inline_data", out inlineData))
                    continue;
                var mime = inlineData.TryGetProperty("mimeType", out var camelMime)
                    ? camelMime.GetString()
                    : inlineData.TryGetProperty("mime_type", out var snakeMime) ? snakeMime.GetString() : null;
                if (!string.Equals(mime, "image/png", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mime, "image/jpeg", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mime, "image/jpg", StringComparison.OrdinalIgnoreCase))
                    throw Result("Služba vrátila nepodporovaný formát obrázku. Původní obrázek zůstal beze změny.");
                if (!inlineData.TryGetProperty("data", out var data))
                    throw Result("Odpověď služby neobsahuje data obrázku. Původní obrázek zůstal beze změny.");
                try
                {
                    return (Convert.FromBase64String(data.GetString() ?? string.Empty), mime!);
                }
                catch (FormatException exception)
                {
                    throw Result("Odpověď služby obsahuje neplatný obrázek. Původní obrázek zůstal beze změny.", exception);
                }
            }
        }

        throw Result("Služba nevrátila žádný obrázek. Původní obrázek zůstal beze změny.");
    }

    private static Task WriteMaskedPngAsync(
        byte[] maskBytes,
        string maskMimeType,
        byte[] originalBytes,
        string originalExtension,
        string destinationPath,
        int sourceWidth,
        int sourceHeight,
        CancellationToken cancellationToken)
    {
        var maskExtension = maskMimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        var (maskWidth, maskHeight) = ReadMaskDimensions(maskBytes, maskExtension);
        if ((long)maskWidth * maskHeight > MaximumPixelCount)
            throw Result("Maska Gemini je příliš velká.");
        var sourceRatio = sourceWidth / (double)sourceHeight;
        var maskRatio = maskWidth / (double)maskHeight;
        if (Math.Abs(sourceRatio - maskRatio) / sourceRatio > 0.01)
            throw Result("Gemini změnilo poměr stran obrázku. Výsledek nebyl použit.");

        using var sourceStream = new MemoryStream(originalBytes, writable: false);
        using var decodedSource = new Bitmap(sourceStream);
        if (decodedSource.Width != sourceWidth || decodedSource.Height != sourceHeight)
            throw Result("Zdrojový obrázek změnil rozměry během zpracování.");
        using var maskStream = new MemoryStream(maskBytes, writable: false);
        using var decodedMask = new Bitmap(maskStream);
        using var source = ToArgbBitmap(decodedSource, sourceWidth, sourceHeight);
        using var mask = ToArgbBitmap(decodedMask, sourceWidth, sourceHeight);

        var invertMask = ValidateMask(mask, cancellationToken);
        using var output = new Bitmap(sourceWidth, sourceHeight, PixelFormat.Format32bppArgb);
        ApplyMask(source, mask, output, invertMask, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("Neplatná cílová cesta obrázku.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            output.Save(temporaryPath, ImageFormat.Png);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullDestinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        return Task.CompletedTask;
    }

    private static Bitmap ToArgbBitmap(Bitmap source, int width, int height)
    {
        var target = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(target);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return target;
    }

    private static bool ValidateMask(Bitmap mask, CancellationToken cancellationToken)
    {
        var rectangle = new Rectangle(0, 0, mask.Width, mask.Height);
        var data = mask.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        long borderLuminance = 0;
        long borderPixelCount = 0;
        long nonGrayscalePixelCount = 0;
        long foregroundPixelCount = 0;
        long backgroundPixelCount = 0;
        long backgroundBorderPixelCount = 0;
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            for (var y = 0; y < mask.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < mask.Width; x++)
                {
                    var offset = x * 4;
                    var blue = row[offset];
                    var green = row[offset + 1];
                    var red = row[offset + 2];
                    var luminance = (red * 299 + green * 587 + blue * 114) / 1000;
                    if (Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) > 48)
                        nonGrayscalePixelCount++;
                    if (x == 0 || x == mask.Width - 1 || y == 0 || y == mask.Height - 1)
                    {
                        borderLuminance += luminance;
                        borderPixelCount++;
                    }
                }
            }

            var invertMask = borderLuminance / (double)borderPixelCount > 127;
            for (var y = 0; y < mask.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 0; x < mask.Width; x++)
                {
                    var offset = x * 4;
                    var blue = row[offset];
                    var green = row[offset + 1];
                    var red = row[offset + 2];
                    var luminance = (red * 299 + green * 587 + blue * 114) / 1000;
                    if (invertMask) luminance = 255 - luminance;
                    if (luminance >= 223) foregroundPixelCount++;
                    if (luminance <= 32)
                    {
                        backgroundPixelCount++;
                        if (x == 0 || x == mask.Width - 1 || y == 0 || y == mask.Height - 1)
                            backgroundBorderPixelCount++;
                    }
                }
            }

            var pixelCount = (long)mask.Width * mask.Height;
            var minimumRegionPixels = Math.Max(16, pixelCount / 1000);
            return (double)nonGrayscalePixelCount / pixelCount <= 0.1 &&
                   foregroundPixelCount >= minimumRegionPixels &&
                   backgroundPixelCount >= minimumRegionPixels &&
                   backgroundBorderPixelCount >= Math.Max(8, borderPixelCount / 50)
                ? invertMask
                : throw Result("Gemini nevrátilo použitelnou černobílou masku. Původní obrázek zůstal beze změny.");
        }
        finally
        {
            mask.UnlockBits(data);
        }
    }

    private static void ApplyMask(
        Bitmap source, Bitmap mask, Bitmap output, bool invertMask, CancellationToken cancellationToken)
    {
        var rectangle = new Rectangle(0, 0, source.Width, source.Height);
        var sourceData = source.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var maskData = mask.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var outputData = output.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var sourceRow = new byte[Math.Abs(sourceData.Stride)];
            var maskRow = new byte[Math.Abs(maskData.Stride)];
            var outputRow = new byte[Math.Abs(outputData.Stride)];
            for (var y = 0; y < source.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Marshal.Copy(sourceData.Scan0 + y * sourceData.Stride, sourceRow, 0, sourceRow.Length);
                Marshal.Copy(maskData.Scan0 + y * maskData.Stride, maskRow, 0, maskRow.Length);
                for (var x = 0; x < source.Width; x++)
                {
                    var offset = x * 4;
                    var blue = maskRow[offset];
                    var green = maskRow[offset + 1];
                    var red = maskRow[offset + 2];
                    var luminance = (red * 299 + green * 587 + blue * 114) / 1000;
                    if (invertMask) luminance = 255 - luminance;
                    var maskAlpha = luminance switch
                    {
                        <= 24 => 0,
                        >= 231 => 255,
                        _ => (luminance - 24) * 255 / 207,
                    };
                    outputRow[offset] = sourceRow[offset];
                    outputRow[offset + 1] = sourceRow[offset + 1];
                    outputRow[offset + 2] = sourceRow[offset + 2];
                    outputRow[offset + 3] = (byte)(sourceRow[offset + 3] * maskAlpha / 255);
                }
                Marshal.Copy(outputRow, 0, outputData.Scan0 + y * outputData.Stride, outputRow.Length);
            }
        }
        finally
        {
            output.UnlockBits(outputData);
            mask.UnlockBits(maskData);
            source.UnlockBits(sourceData);
        }
    }
    private static (int Width, int Height) ReadMaskDimensions(byte[] bytes, string extension)
    {
        try
        {
            return ReadDimensions(bytes, extension);
        }
        catch (InvalidDataException exception)
        {
            throw Result("Služba vrátila poškozený obrázek. Původní obrázek zůstal beze změny.", exception);
        }
    }

    private static (byte[] Bytes, string MimeType) NormalizeUploadChecked(byte[] bytes, string extension)
    {
        try
        {
            return NormalizeUpload(bytes, extension);
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException or OutOfMemoryException)
        {
            throw new BackgroundRemovalException(BackgroundRemovalFailure.InvalidImage,
                "Obrázek se nepodařilo přečíst. Zkuste jiný soubor.", exception);
        }
    }

    private static (int Width, int Height) ReadDimensions(byte[] bytes, string extension)
    {
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            return ReadPngDimensions(bytes);
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            return ReadJpegDimensions(bytes);
        if (extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
            return ReadBmpDimensions(bytes);
        throw new InvalidDataException("Podporujeme pouze PNG, JPG a BMP.");
    }

    private static (int Width, int Height) ReadPngDimensions(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(signature) ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("Soubor není platný PNG obrázek.");

        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
            throw new InvalidDataException("PNG obrázek má neplatné rozměry.");
        return ((int)width, (int)height);
    }

    private static (int Width, int Height) ReadBmpDimensions(byte[] bytes)
    {
        if (bytes.Length < 26 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
            throw new InvalidDataException("Soubor není platný BMP obrázek.");

        var dibHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14, 4));
        if (dibHeaderSize == 12) // BITMAPCOREHEADER uses unsigned 16-bit dimensions.
        {
            var coreWidth = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18, 2));
            var coreHeight = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20, 2));
            if (coreWidth == 0 || coreHeight == 0)
                throw new InvalidDataException("BMP obrázek má neplatné rozměry.");
            return (coreWidth, coreHeight);
        }
        if (dibHeaderSize < 40 || bytes.Length < 54)
            throw new InvalidDataException("BMP obrázek používá nepodporovanou hlavičku.");

        var width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(18, 4));
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(22, 4));
        var height = Math.Abs((long)signedHeight);
        if (width <= 0 || height <= 0 || height > int.MaxValue)
            throw new InvalidDataException("BMP obrázek má neplatné rozměry.");
        return (width, (int)height);
    }

    private static (int Width, int Height) ReadJpegDimensions(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            throw new InvalidDataException("Soubor není platný JPEG obrázek.");

        var offset = 2;
        while (offset < bytes.Length)
        {
            while (offset < bytes.Length && bytes[offset] != 0xFF) offset++;
            while (offset < bytes.Length && bytes[offset] == 0xFF) offset++;
            if (offset >= bytes.Length) break;

            var marker = bytes[offset++];
            if (marker is 0xD8 or 0xD9 or 0x01 or >= 0xD0 and <= 0xD7) continue;
            if (offset + 2 > bytes.Length) break;

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > bytes.Length) break;
            if (IsJpegStartOfFrame(marker))
            {
                if (segmentLength < 7) break;
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2));
                if (width == 0 || height == 0) break;
                return (width, height);
            }

            if (marker == 0xDA) break; // Start of scan before a valid frame header.
            offset += segmentLength;
        }

        throw new InvalidDataException("JPEG obrázek nemá platný rozměrový blok.");
    }

    private static bool IsJpegStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static string GetDestinationPath(byte[] sourceBytes)
    {
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes))[..16];
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lasero", "background-removal", "gemini");
        return Path.Combine(directory, $"{sourceHash}-{Guid.NewGuid():N}.nobg.png");
    }
}
