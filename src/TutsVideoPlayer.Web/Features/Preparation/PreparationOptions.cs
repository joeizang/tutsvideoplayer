namespace TutsVideoPlayer.Web.Features.Preparation;

public sealed class PreparationOptions
{
    public const string SectionName = "Preparation";

    public string FFmpegPath { get; init; } = "ffmpeg";

    /// <summary>Free space that must remain on the output volume for preparation to start.</summary>
    public long DiskReserveBytes { get; init; } = 2_000_000_000;
}
