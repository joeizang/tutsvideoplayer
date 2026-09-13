using TutsVideoPlayer.Infrastructure.Media;

namespace TutsVideoPlayer.IntegrationTests;

public class FFprobeAdapterTests
{
    [Fact]
    public async Task ProbesRealFixtureWithStreamsAndDuration()
    {
        var fixturePath = FindDemoFixture()
            ?? throw new InvalidOperationException("The demo fixture was not found.");
        var adapter = new FFprobeAdapter("ffprobe");

        var result = await adapter.ProbeAsync(fixturePath, TestContext.Current.CancellationToken);

        Assert.True(result.Success, probeError(result));
        Assert.InRange(result.DurationMs ?? 0, 9000, 12000);
        Assert.Equal("h264", result.VideoCodec);
        Assert.Equal("aac", result.AudioCodec);
        Assert.Equal(320, result.Width);
        Assert.Equal(180, result.Height);
    }

    [Fact]
    public async Task ProbeOfGarbageFileReportsFailure()
    {
        var garbagePath = Path.Combine(Path.GetTempPath(), $"tuts-garbage-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(garbagePath, [1, 2, 3], TestContext.Current.CancellationToken);
        try
        {
            var adapter = new FFprobeAdapter("ffprobe");
            var result = await adapter.ProbeAsync(garbagePath, TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.NotNull(result.Error);
        }
        finally
        {
            File.Delete(garbagePath);
        }
    }

    private static string? FindDemoFixture() =>
        new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Media", "demo-fixture.mp4"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "TutsVideoPlayer.Web", "Media", "demo-fixture.mp4")
        }.FirstOrDefault(File.Exists);

    private static string probeError(MediaProbeResult result) => result.Error ?? "probe failed without an error";
}