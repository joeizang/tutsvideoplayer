using System.Text;

namespace TutsVideoPlayer.IntegrationTests;

public static class FixtureLibraryBuilder
{
    private static string? FindDemoFixture() =>
        new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "TutsVideoPlayer.Web", "Media", "demo-fixture.mp4")
        }.FirstOrDefault(File.Exists);

    public static string Build(string rootDirectory)
    {
        Directory.CreateDirectory(rootDirectory);

        var demoFixture = FindDemoFixture();
        if (demoFixture is not null)
        {
            var realCourse = Path.Combine(rootDirectory, "RealCourse");
            Directory.CreateDirectory(realCourse);
            File.Copy(demoFixture, Path.Combine(realCourse, "01 Real Lesson.mp4"));
        }

        var alpha = Path.Combine(rootDirectory, "CourseAlpha");
        WriteFile(Path.Combine(alpha, "01 Intro", "02 Getting Started.mp4"), [1, 2, 3, 4]);
        WriteFile(Path.Combine(alpha, "03 Setup", "10 Advanced.mp4"), [5, 6, 7, 8]);
        WriteFile(Path.Combine(alpha, "03 Setup", "02 Basics.mp4"), [9, 10, 11, 12]);
        WriteFile(Path.Combine(alpha, ".DS_Store"), [0]);
        WriteFile(Path.Combine(alpha, "notes.txt"), "notes"u8.ToArray());

        var tsCourse = Path.Combine(rootDirectory, "CourseTS");
        WriteFile(Path.Combine(tsCourse, "lessonA.ts"), [1]);
        WriteFile(Path.Combine(tsCourse, "lessonA_audio.aac"), [2]);
        WriteFile(Path.Combine(tsCourse, "lessonB.ts"), [3]);
        WriteFile(Path.Combine(tsCourse, "lessonC.ts"), [4]);
        WriteFile(Path.Combine(tsCourse, "lessonC_audio.aac"), [5]);
        WriteFile(Path.Combine(tsCourse, "orphan_audio.aac"), [6]);

        var subsCourse = Path.Combine(rootDirectory, "CourseSubs");
        WriteFile(Path.Combine(subsCourse, "video.mp4"), [1]);
        WriteFile(Path.Combine(subsCourse, "video.srt"), Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:01,000\nhi\n"));
        WriteFile(Path.Combine(subsCourse, "video.vtt"), Encoding.UTF8.GetBytes("WEBVTT\n\n00:00.000 --> 00:01.000\nhi\n"));
        WriteFile(Path.Combine(subsCourse, "unrelated.srt"), Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:01,000\nhi\n"));
        WriteFile(Path.Combine(subsCourse, "broken.srt"), Encoding.UTF8.GetBytes("1\nnot a timing line\ntext\n"));

        WriteFile(Path.Combine(alpha, "01 Intro", "Subtitles", "02 Getting Started.srt"),
            Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:01,000\nfolder cue\n"));
        WriteFile(Path.Combine(alpha, "03 Setup", "10 Advanced.en.srt"),
            Encoding.UTF8.GetBytes("1\n00:00:00,000 --> 00:00:01,000\nenglish cue\n"));

        var empty = Path.Combine(rootDirectory, "EmptyDirectory");
        Directory.CreateDirectory(empty);
        WriteFile(Path.Combine(empty, "readme.txt"), "no videos here"u8.ToArray());

        WriteFile(Path.Combine(rootDirectory, "loose.mp4"), [1]);
        WriteFile(Path.Combine(rootDirectory, ".tutsvideoplayer", "cache.mp4"), [1]);
        WriteFile(Path.Combine(rootDirectory, "archive.zip"), [0]);

        return rootDirectory;
    }

    public static string BuildTemporary()
    {
        var root = Directory.CreateTempSubdirectory("tuts-fixture-").FullName;
        return Build(root);
    }

    private static void WriteFile(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}