using TutsVideoPlayer.Core.Catalog;

namespace TutsVideoPlayer.Core.Tests;

public class NaturalSortKeyTests
{
    [Theory]
    [InlineData("Lesson 2", "Lesson 10")]
    [InlineData("02 Getting Started.mp4", "10 Advanced.mp4")]
    [InlineData("a/2", "a/10")]
    [InlineData("01 Intro", "01 Introduction")]
    public void DigitRunsCompareNumerically(string earlier, string later)
    {
        Assert.True(string.CompareOrdinal(
            NaturalSortKey.Generate(earlier),
            NaturalSortKey.Generate(later)) < 0);
    }

    [Theory]
    [InlineData("Lesson 10", "Lesson 2")]
    [InlineData("10 Advanced.mp4", "02 Getting Started.mp4")]
    public void DigitRunsRejectLexicographicOrder(string earlier, string later)
    {
        Assert.True(string.CompareOrdinal(
            NaturalSortKey.Generate(earlier),
            NaturalSortKey.Generate(later)) > 0);
    }

    [Fact]
    public void SameNumericValueProducesEqualKeys()
    {
        Assert.Equal(NaturalSortKey.Generate("1"), NaturalSortKey.Generate("01"));
        Assert.Equal(NaturalSortKey.Generate("001"), NaturalSortKey.Generate("1"));
    }

    [Fact]
    public void OrdinalTieBreakerSeparatesNamesWithEqualNumericRuns()
    {
        var first = NaturalSortKey.Generate("Episode 1 - Intro.mp4");
        var second = NaturalSortKey.Generate("Episode 1 - Setup.mp4");
        Assert.True(string.CompareOrdinal(first, second) < 0);
    }

    [Fact]
    public void LongDigitRunsAreTruncatedDeterministically()
    {
        var longRun = NaturalSortKey.Generate($"x{new string('9', 30)}");
        var truncatedRun = NaturalSortKey.Generate($"x{new string('9', 18)}");
        Assert.Equal(truncatedRun, longRun);
    }

    [Fact]
    public void KeyIsStableAcrossCalls()
    {
        Assert.Equal(NaturalSortKey.Generate("Setup 12 Final.mp4"), NaturalSortKey.Generate("Setup 12 Final.mp4"));
    }
}