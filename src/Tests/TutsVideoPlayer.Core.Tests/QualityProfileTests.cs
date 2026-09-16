using TutsVideoPlayer.Core.Preparation;

namespace TutsVideoPlayer.Core.Tests;

public class QualityProfileTests
{
    [Fact]
    public void Source720NeverClaims1080Or720()
    {
        var eligible = QualityProfiles.EligibleProfiles(1280, 720);
        Assert.DoesNotContain("1080", eligible);
        Assert.DoesNotContain("720", eligible);
        Assert.Equal(["480"], eligible);
    }

    [Fact]
    public void Source768Offers720And480ButNot1080()
    {
        var eligible = QualityProfiles.EligibleProfiles(1024, 768);
        Assert.Equal(["720", "480"], eligible);
    }

    [Fact]
    public void Source1080Offers720And480()
    {
        var eligible = QualityProfiles.EligibleProfiles(1920, 1080);
        Assert.DoesNotContain("1080", eligible);
        Assert.Equal(["720", "480"], eligible);
    }

    [Fact]
    public void SourceAbove1080Offers1080()
    {
        var eligible = QualityProfiles.EligibleProfiles(3840, 2160);
        Assert.Contains("1080", eligible);
        Assert.Contains("720", eligible);
        Assert.Contains("480", eligible);
    }

    [Fact]
    public void ScaledDimensionsPreserveAspectAndStayEven()
    {
        var (width, height) = QualityProfiles.ScaledDimensions(1024, 768, 480);
        Assert.Equal(480, height);
        Assert.Equal(640, width);
        Assert.Equal(0, width % 2);
        Assert.Equal(0, height % 2);
    }

    [Fact]
    public void ScaledDimensionsNeverUpscale()
    {
        Assert.Throws<ArgumentException>(() => QualityProfiles.ScaledDimensions(1280, 720, 1080));
    }

    [Theory]
    [InlineData("1080", 1080)]
    [InlineData("720", 720)]
    [InlineData("480", 480)]
    [InlineData("native", null)]
    public void TargetHeightMapsKnownProfiles(string profile, int? expected)
    {
        Assert.Equal(expected, QualityProfiles.TargetHeight(profile));
    }

    [Fact]
    public void LabelsReportActualDimensionsForNonstandardSources()
    {
        Assert.Equal("Original (1024×768)", QualityProfiles.LabelFor(null, 1024, 768));
        Assert.Equal("720p (1024×576)", QualityProfiles.LabelFor("720", 1024, 576));
        Assert.Equal("1080p", QualityProfiles.LabelFor("1080", null, null));
    }
}