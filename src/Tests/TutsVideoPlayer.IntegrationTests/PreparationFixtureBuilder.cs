using System.Diagnostics;
using System.Text;

namespace TutsVideoPlayer.IntegrationTests;

/// <summary>
/// Builds a fixture whose lessons require real preparation work (WMV encode,
/// split TS/AAC mapping, an exempt MP4). Requires ffmpeg on the host; the
/// generated clips are a couple of seconds each so encodes stay fast.
/// </summary>
public static class PreparationFixtureBuilder
{
    public static bool FFmpegAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string Build(string rootDirectory)
    {
        Directory.CreateDirectory(rootDirectory);
        if (!FFmpegAvailable())
        {
            throw new InvalidOperationException("ffmpeg is required for preparation tests but was not found on PATH.");
        }


        var course = Path.Combine(rootDirectory, "CoursePrep");
        Directory.CreateDirectory(course);

        RunFFmpeg("-f", "lavfi", "-i", "testsrc2=duration=2:size=160x90:rate=12",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-c:v", "wmv2", "-b:v", "160k", "-c:a", "wmav2", "-shortest",
            Path.Combine(course, "01 WMV Lesson.wmv"));

        RunFFmpeg("-f", "lavfi", "-i", "testsrc2=duration=2:size=160x90:rate=12",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            Path.Combine(course, "02 TS Lesson.ts"));

        RunFFmpeg("-f", "lavfi", "-i", "sine=frequency=550:duration=2",
            "-c:a", "aac", "-b:a", "64k",
            Path.Combine(course, "02 TS Lesson_audio.aac"));

        RunFFmpeg("-f", "lavfi", "-i", "testsrc2=duration=2:size=160x90:rate=12",
            "-f", "lavfi", "-i", "sine=frequency=660:duration=2",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "64k", "-shortest",
            Path.Combine(course, "03 Exempt Lesson.mp4"));

        RunFFmpeg("-f", "lavfi", "-i", "testsrc2=duration=1:size=1920x1080:rate=12",
            "-f", "lavfi", "-i", "sine=frequency=770:duration=1",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "64k", "-shortest",
            Path.Combine(course, "04 Tall Lesson.mp4"));

        return rootDirectory;
    }

    public static string BuildTemporary() => Build(Directory.CreateTempSubdirectory("tuts-prep-").FullName);

    private static void RunFFmpeg(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffmpeg could not be started.");
        if (!process.WaitForExit(60_000) || process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed while generating a preparation fixture (exit {process.ExitCode}).");
        }
    }
}