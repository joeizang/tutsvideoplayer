namespace TutsVideoPlayer.Core.Learning;

public enum CompletionChoice
{
    Automatic = 0,
    Completed = 1,
    Incomplete = 2
}

public static class PlaybackSpeeds
{
    public static readonly IReadOnlyList<double> Allowed = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];

    public static bool IsAllowed(double value) => Allowed.Any(speed => Math.Abs(speed - value) < 0.001);
}

public static class CompletionResolver
{
    public static bool IsEffectivelyComplete(bool automaticCompleted, CompletionChoice? manualCompletion) =>
        manualCompletion switch
        {
            CompletionChoice.Completed => true,
            CompletionChoice.Incomplete => false,
            _ => automaticCompleted
        };
}
