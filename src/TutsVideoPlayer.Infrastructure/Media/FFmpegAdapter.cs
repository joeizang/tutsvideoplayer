using System.Diagnostics;

namespace TutsVideoPlayer.Infrastructure.Media;

public sealed record FFmpegResult(int? ExitCode, bool Completed, string? Error, double? LastProgressSeconds)
{
    public bool Success => Completed && ExitCode == 0;
}

/// <summary>
/// Bounded FFmpeg child-process adapter. Arguments are structured through
/// ArgumentList, shell execution is disabled, streams are drained concurrently,
/// progress is parsed from `-progress pipe:1`, and a no-progress watchdog kills
/// the owned process tree. CPU time alone is never failure.
/// </summary>
public sealed class FFmpegAdapter(string ffmpegPath)
{
    private static readonly TimeSpan NoProgressWatchdog = TimeSpan.FromSeconds(120);
    private const int DiagnosticTailLength = 2000;

    public async Task<FFmpegResult> RunAsync(
        IReadOnlyList<string> arguments,
        Action<double>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add("-nostats");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            return new FFmpegResult(null, Completed: false, $"ffmpeg could not be started: {exception.Message}", null);
        }

        var lastProgressUtc = Environment.TickCount64;
        double lastProgressSeconds = 0;

        var stdoutTask = ConsumeProgressAsync(process, seconds =>
        {
            lastProgressUtc = Environment.TickCount64;
            lastProgressSeconds = seconds;
            progress?.Invoke(seconds);
        }, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var watchdog = new Timer(_ =>
        {
            if (Environment.TickCount64 - Volatile.Read(ref lastProgressUtc) > NoProgressWatchdog.TotalMilliseconds)
            {
                watchdogCts.Cancel();
            }
        }, null, NoProgressWatchdog, NoProgressWatchdog);

        try
        {
            await process.WaitForExitAsync(watchdogCts.Token);

            if (!cancellationToken.IsCancellationRequested && watchdogCts.IsCancellationRequested)
            {
                await KillAsync(process);
                return new FFmpegResult(
                    null,
                    Completed: false,
                    $"ffmpeg made no progress for {NoProgressWatchdog.TotalSeconds:0} seconds and was stopped.",
                    lastProgressSeconds);
            }
        }
        catch (OperationCanceledException)
        {
            await KillAsync(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new FFmpegResult(null, Completed: false, "ffmpeg was stopped by the no-progress watchdog.", lastProgressSeconds);
        }

        await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var tail = stderr.Length > DiagnosticTailLength ? stderr[^DiagnosticTailLength..] : stderr;
            return new FFmpegResult(process.ExitCode, Completed: true, $"ffmpeg exited with code {process.ExitCode}: {tail.Trim()}", lastProgressSeconds);
        }

        return new FFmpegResult(0, Completed: true, null, lastProgressSeconds);
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

    private static async Task ConsumeProgressAsync(Process process, Action<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line["out_time_us=".Length..], out var microseconds))
                {
                    progress(Math.Max(0, microseconds / 1_000_000.0));
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or IOException)
        {
        }
    }
}