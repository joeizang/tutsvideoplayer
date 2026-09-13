using TutsVideoPlayer.Core.Catalog;

namespace TutsVideoPlayer.Core.Tests;

public class RelativeMediaPathTests
{
    [Theory]
    [InlineData("Course A/lesson.mp4")]
    [InlineData("lesson.mp4")]
    [InlineData("Course A/01 Intro/video 01.mp4")]
    public void AcceptsRelativePathsWithoutTraversal(string candidate)
    {
        Assert.True(RelativeMediaPath.TryCreate(candidate, out _));
    }

    [Theory]
    [InlineData("/absolute/path.mp4")]
    [InlineData("../outside.mp4")]
    [InlineData("a/../b.mp4")]
    [InlineData("./current.mp4")]
    [InlineData("a//b.mp4")]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsRootTraversalAndEmptySegments(string candidate)
    {
        Assert.False(RelativeMediaPath.TryCreate(candidate, out _));
    }

    [Fact]
    public void DriveRootIsRejectedOnlyOnWindows()
    {
        var expectedValid = !OperatingSystem.IsWindows();
        Assert.Equal(expectedValid, RelativeMediaPath.TryCreate("C:\\drive\\path.mp4", out _));
    }

    [Fact]
    public void BackslashIsFilenameCharacterOnUnix()
    {
        var expectedValid = !OperatingSystem.IsWindows();
        Assert.Equal(expectedValid, RelativeMediaPath.TryCreate(@"a\b.mp4\c", out _));
    }

    [Fact]
    public void SegmentsSplitOnBothSeparators()
    {
        var path = RelativeMediaPath.Create("Course/01 Intro/video.mp4");
        Assert.Equal(3, path.Segments.Count);
    }

    [Fact]
    public void CreateThrowsForInvalidCandidate()
    {
        Assert.Throws<ArgumentException>(() => RelativeMediaPath.Create("../escape.mp4"));
    }
}