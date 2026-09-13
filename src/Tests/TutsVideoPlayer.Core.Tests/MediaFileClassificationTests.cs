using TutsVideoPlayer.Core.Catalog;

namespace TutsVideoPlayer.Core.Tests;

public class MediaFileClassificationTests
{
    [Theory]
    [InlineData("lesson.mp4", MediaFileKind.Video)]
    [InlineData("lesson.MKV", MediaFileKind.Video)]
    [InlineData("clip.wmv", MediaFileKind.Video)]
    [InlineData("segment.ts", MediaFileKind.Video)]
    [InlineData("capture.mts", MediaFileKind.Video)]
    [InlineData("notes.srt", MediaFileKind.Subtitle)]
    [InlineData("notes.vtt", MediaFileKind.Subtitle)]
    [InlineData("lesson_audio.aac", MediaFileKind.CompanionAudio)]
    [InlineData("audio.aac", MediaFileKind.Ignored)]
    public void ClassifiesKnownKinds(string fileName, MediaFileKind expected)
    {
        Assert.Equal(expected, MediaFileClassification.Classify(fileName));
    }

    [Theory]
    [InlineData(".DS_Store")]
    [InlineData(".hidden")]
    [InlineData("backup.zip")]
    [InlineData("notes.txt")]
    [InlineData("preview.jpg")]
    [InlineData("metadata.nfo")]
    [InlineData("archive.zip")]
    [InlineData("download.part")]
    public void SupportAndJunkFilesAreIgnored(string fileName)
    {
        Assert.Equal(MediaFileKind.Ignored, MediaFileClassification.Classify(fileName));
    }

    [Fact]
    public void ReservedAppDirectoryIsRecognized()
    {
        Assert.True(MediaFileClassification.IsReservedDirectory(".tutsvideoplayer"));
        Assert.False(MediaFileClassification.IsReservedDirectory("tutsvideoplayer"));
    }

    [Fact]
    public void CompanionAudioNameFollowsConvention()
    {
        Assert.Equal("lesson_audio.aac", MediaFileClassification.CompanionAudioNameFor("lesson.ts"));
        Assert.Equal("lesson", MediaFileClassification.VideoStemFor("lesson_audio.aac"));
    }
}