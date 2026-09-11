using System.IO;

namespace Lasero.Core.BackgroundRemoval;

/// <summary>
/// Locates and downloads the local segmentation model background removal runs on. Mirrors
/// AppSettingsStore's own convention (a directory-bearing type with a CreateDefault() pointing at
/// %LOCALAPPDATA%\Lasero) so the model cache lives alongside settings.json and project-assets under
/// the same app-local root instead of inventing a new storage convention.
///
/// isnet-general-use.onnx is Apache License 2.0 (from the DIS project, https://github.com/xuebinqin/DIS,
/// "Our code and evaluation metric use Apache License 2.0"), redistributed as a public, ungated GitHub
/// release asset by the rembg project (MIT-licensed, https://github.com/danielgatis/rembg). rembg's own
/// model download table (rembg/sessions/base.py) names this exact URL as the download source.
/// </summary>
public sealed class BackgroundRemovalModelStore
{
    public const string ModelFileName = "isnet-general-use.onnx";

    public const string ModelDownloadUrl =
        "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx";

    private readonly string _directory;

    public BackgroundRemovalModelStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public static BackgroundRemovalModelStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lasero", "Models"));

    public string ModelPath => Path.Combine(_directory, ModelFileName);

    /// <summary>True once the model has been fully downloaded. A zero-length file (e.g. left behind
    /// by an interrupted write that skipped the temp-file/move dance) does not count as available.</summary>
    public bool IsModelAvailable => File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 0;

    /// <summary>Downloads the model to the cache directory, reporting 0..1 progress when the server
    /// sends a Content-Length. Writes to a temp file first and moves it into place on success, so a
    /// cancelled or failed download never leaves a partial file that IsModelAvailable would accept.</summary>
    public async Task DownloadModelAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var temporaryPath = Path.Combine(_directory, $".{ModelFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var client = new HttpClient();
            using var response = await client
                .GetAsync(ModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var output = new FileStream(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long readBytes = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    readBytes += read;
                    if (totalBytes is > 0) progress?.Report((double)readBytes / totalBytes.Value);
                }
            }

            File.Move(temporaryPath, ModelPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
