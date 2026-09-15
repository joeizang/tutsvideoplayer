namespace TutsVideoPlayer.Core.Preparation;

public enum PreparationJobState
{
    Queued = 0,
    Running = 1,
    Validating = 2,
    Publishing = 3,
    Blocked = 4,
    Interrupted = 5,
    Failed = 6,
    Succeeded = 7,
    Canceled = 8
}

/// <summary>
/// The durable job state machine from the domain model. Every transition is
/// explicit; anything not listed is rejected by callers instead of happening
/// through a silent setter.
/// </summary>
public static class PreparationTransitions
{
    public static bool CanTransition(PreparationJobState from, PreparationJobState to) => (from, to) switch
    {
        (PreparationJobState.Queued, PreparationJobState.Running) => true,
        (PreparationJobState.Queued, PreparationJobState.Blocked) => true,
        (PreparationJobState.Queued, PreparationJobState.Canceled) => true,
        (PreparationJobState.Blocked, PreparationJobState.Queued) => true,
        (PreparationJobState.Running, PreparationJobState.Validating) => true,
        (PreparationJobState.Running, PreparationJobState.Interrupted) => true,
        (PreparationJobState.Running, PreparationJobState.Failed) => true,
        (PreparationJobState.Validating, PreparationJobState.Publishing) => true,
        (PreparationJobState.Validating, PreparationJobState.Failed) => true,
        (PreparationJobState.Publishing, PreparationJobState.Succeeded) => true,
        (PreparationJobState.Publishing, PreparationJobState.Interrupted) => true,
        (PreparationJobState.Interrupted, PreparationJobState.Queued) => true,
        (PreparationJobState.Interrupted, PreparationJobState.Succeeded) => true,
        (PreparationJobState.Failed, PreparationJobState.Queued) => true,
        _ => false
    };

    /// <summary>
    /// Jobs a worker may claim: queued work, plus interrupted work whose recovery
    /// decision (requeue or adopt) has already been made.
    /// </summary>
    public static bool IsClaimable(PreparationJobState state) => state == PreparationJobState.Queued;
}
