using TutsVideoPlayer.Core.Subtitles;

namespace TutsVideoPlayer.Core.Tests;

public class SubtitleConverterTests
{
    [Fact]
    public void ConvertsSrtToWebVttPreservingTimings()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:02,500\nHello there\n\n2\n00:00:03,000 --> 00:00:04,000\nSecond cue\n";

        var result = SubtitleConverter.ConvertToWebVtt(srt, isAlreadyWebVtt: false);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.CueCount);
        Assert.Contains("WEBVTT", result.WebVtt);
        Assert.Contains("00:00:01.000 --> 00:00:02.500", result.WebVtt);
        Assert.Contains("Hello there", result.WebVtt);
        Assert.Contains("00:00:03.000 --> 00:00:04.000", result.WebVtt);
        Assert.DoesNotContain(",", result.WebVtt!.Split('\n').First(line => line.Contains("-->", StringComparison.Ordinal)));
    }

    [Fact]
    public void HandlesCrlfAndOptionalIndexLines()
    {
        const string srt = "00:00:01.000 --> 00:00:02.000\r\nNo index line\r\n\r\n00:00:03.000 --> 00:00:04.000\r\nSecond\r\n";

        var result = SubtitleConverter.ConvertToWebVtt(srt, isAlreadyWebVtt: false);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.CueCount);
    }

    [Fact]
    public void ValidatesWebVttHeader()
    {
        var result = SubtitleConverter.ConvertToWebVtt("00:00:01.000 --> 00:00:02.000\nText", isAlreadyWebVtt: true);
        Assert.False(result.Success);
        Assert.Contains("WEBVTT", result.Error);
    }

    [Fact]
    public void ConvertsVttKeepingCueSettingsAndSkippingNotes()
    {
        const string vtt = "WEBVTT\n\nNOTE this is a comment\n\nintro\n00:00:01.000 --> 00:00:02.000 line:0 position:50%\nText here\n\n00:00:03.000 --> 00:00:04.000\nAnother\n";

        var result = SubtitleConverter.ConvertToWebVtt(vtt, isAlreadyWebVtt: true);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.CueCount);
        Assert.Contains("line:0 position:50%", result.WebVtt);
        Assert.DoesNotContain("NOTE", result.WebVtt);
    }

    [Theory]
    [InlineData("1\nnot a timing line\nText\n")]
    [InlineData("1\n00:00:05,000 --> 00:00:02,000\nEnds before start\n")]
    [InlineData("1\n00:00:xx,000 --> 00:00:02,000\nBad timestamp\n")]
    [InlineData("1\n00:00:61,000 --> 00:00:62,000\nInvalid seconds\n")]
    public void MalformedSrtIsRejectedWithAnExplanation(string srt)
    {
        var result = SubtitleConverter.ConvertToWebVtt(srt, isAlreadyWebVtt: false);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void EscapesUnsafeMarkupButKeepsSupportedInlineTags()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:02,000\n<b>bold</b> and <script>alert(1)</script> and <i>italic</i>\n";

        var result = SubtitleConverter.ConvertToWebVtt(srt, isAlreadyWebVtt: false);

        Assert.True(result.Success, result.Error);
        Assert.Contains("<b>bold</b>", result.WebVtt);
        Assert.Contains("<i>italic</i>", result.WebVtt);
        Assert.DoesNotContain("<script>", result.WebVtt);
        Assert.Contains("&lt;script&gt;", result.WebVtt);
    }

    [Fact]
    public void EscapesAmpersandsAndStandaloneAngleBrackets()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:02,000\nFish & Chips < and >\n";

        var result = SubtitleConverter.ConvertToWebVtt(srt, isAlreadyWebVtt: false);

        Assert.True(result.Success, result.Error);
        Assert.Contains("Fish &amp; Chips &lt; and &gt;", result.WebVtt);
    }

    [Fact]
    public void EnforcesCueCountLimit()
    {
        var blocks = string.Join("\n\n", System.Linq.Enumerable.Range(1, SubtitleLimits.MaxCues + 5)
            .Select(index => $"{index}\n00:00:{index % 60:00},000 --> 00:00:{index % 60:00},500\nCue {index}"));
        blocks += "\n\n";

        var result = SubtitleConverter.ConvertToWebVtt(blocks, isAlreadyWebVtt: false);

        Assert.False(result.Success);
        Assert.Contains("cues", result.Error);
    }

    [Fact]
    public void EnforcesCueTextLengthLimit()
    {
        var longText = new string('x', SubtitleLimits.MaxCueTextChars + 1);
        var srt = $"1\n00:00:01,000 --> 00:00:02,000\n{longText}\n";

        var result = SubtitleConverter.ConvertToWebVtt(srt, isAlreadyWebVtt: false);

        Assert.False(result.Success);
        Assert.Contains("text length", result.Error);
    }

    [Fact]
    public void RejectsBinaryContent()
    {
        var result = SubtitleConverter.ConvertToWebVtt("binary\0content", isAlreadyWebVtt: false);
        Assert.False(result.Success);
        Assert.Contains("binary", result.Error);
    }

    [Theory]
    [InlineData("lesson", "lesson", null)]
    [InlineData("lesson.en", "lesson", "en")]
    [InlineData("lesson.eng", "lesson", "eng")]
    [InlineData("lesson.english", "lesson.english", null)]
    public void SplitsConservativeLanguageSuffixes(string stem, string expectedBase, string? expectedLanguage)
    {
        var split = SubtitleConverter.TrySplitLanguageSuffix(stem, out var baseStem, out var language);
        if (expectedLanguage is null)
        {
            Assert.False(split);
        }

        Assert.Equal(expectedBase, baseStem);
        Assert.Equal(expectedLanguage, language);
    }

    [Fact]
    public void HandlesTimestampsWithoutHours()
    {
        const string vtt = "WEBVTT\n\n01:23.000 --> 01:25.000\nShort form timing\n";

        var result = SubtitleConverter.ConvertToWebVtt(vtt, isAlreadyWebVtt: true);

        Assert.True(result.Success, result.Error);
        Assert.Contains("00:01:23.000 --> 00:01:25.000", result.WebVtt);
    }
}