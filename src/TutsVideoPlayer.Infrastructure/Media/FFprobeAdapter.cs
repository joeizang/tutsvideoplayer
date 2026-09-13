using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TutsVideoPlayer.Infrastructure.Media;

public sealed record MediaProbeResult(
    bool Success,
    long? DurationMs,
    string? VideoCodec,
    string? AudioCodec,
    int? Width,
    int? Height,
    string? Error)
{
    public static MediaProbeResult Failure(string error) => new(false, null, null, null, null, null, error);
}

public sealed record ProbeMetadata(
    long? DurationMs,
    string? VideoCodec,
    string? AudioCodec,
    int? Width,
    int? Height);

public sealed class FFprobeAdapter(string ffprobePath, TimeSpan? timeout = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private const int DiagnosticTailLength = 2000;

    public async Task<MediaProbeResult> ProbeAsync(string absolutePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(absolutePath))
        {
            return MediaProbeResult.Failure("The media file does not exist.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(absolutePath);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            return MediaProbeResult.Failure($"ffprobe could not be started: {exception.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var effectiveTimeout = timeout ?? DefaultTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        string stdout;
        string stderr;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await KillAsync(process);
            return MediaProbeResult.Failure($"ffprobe did not finish within {effectiveTimeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException)
        {
            await KillAsync(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var tail = stderr.Length > DiagnosticTailLength ? stderr[^DiagnosticTailLength..] : stderr;
            return MediaProbeResult.Failure($"ffprobe exited with code {process.ExitCode}: {tail.Trim()}");
        }

        try
        {
            return Parse(stdout);
        }
        catch (JsonException exception)
        {
            return MediaProbeResult.Failure($"ffprobe output could not be parsed: {exception.Message}");
        }
    }

    private static async Task KillAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or SystemException)
        {
        }
    }

    private static MediaProbeResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        long? durationMs = null;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durationElement))
        {
            var durationText = durationElement.ValueKind == JsonValueKind.String
                ? durationElement.GetString()
                : durationElement.GetRawText();
            if (double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSeconds))
            {
                durationMs = (long)Math.Round(durationSeconds * 1000);
            }
        }

        string? videoCodec = null;
        string? audioCodec = null;
        int? width = null;
        int? height = null;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var codecTypeElement))
                {
                    continue;
                }

                var codecType = codecTypeElement.GetString();
                if (codecType == "video" && videoCodec is null)
                {
                    videoCodec = stream.TryGetProperty("codec_name", out var name) ? name.GetString() : null;
                    width = stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var widthValue) ? widthValue : null;
                    height = stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var heightValue) ? heightValue : null;
                }
                else if (codecType == "audio" && audioCodec is null)
                {
                    audioCodec = stream.TryGetProperty("codec_name", out var name) ? name.GetString() : null;
                }
            }
        }

        if (videoCodec is null)
        {
            return MediaProbeResult.Failure("No video stream was reported by ffprobe.");
        }

        return new MediaProbeResult(true, durationMs, videoCodec, audioCodec, width, height, null);
    }
}
