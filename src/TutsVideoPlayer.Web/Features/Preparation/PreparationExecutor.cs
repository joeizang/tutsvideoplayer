using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Core.Preparation;
using TutsVideoPlayer.Infrastructure.Catalog;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Infrastructure.Media;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;
using TutsVideoPlayer.Web.Features.Library;

namespace TutsVideoPlayer.Web.Features.Preparation;

public sealed record PreparationOutcome(PreparationJobState FinalState, string? ErrorCode, string? UserMessage);

/// <summary>
/// Executes one claimed preparation job through the recoverable publication
/// protocol: claim → verify → fingerprint → encode to a temporary file in the
/// destination directory → validate → write Prepared manifest → rename without
/// overwrite → mark Committed → register the ready rendition. Every crash
/// window leaves an explicit reconciliation path.
/// </summary>
public sealed class PreparationExecutor(
    AppDbContext context,
    IOptions<AppOptions> appOptions,
    IOptions<PreparationOptions> preparationOptions,
    FFprobeAdapter probeAdapter,
    FFmpegAdapter ffmpegAdapter,
    PreparedOutputValidator validator,
    ILogger<PreparationExecutor> logger)
{
    private const int MaxAutomaticAttempts = 3;

    public static string DedupKeyFor(long lessonId, int sourceGeneration) =>
        $"{lessonId.ToString(CultureInfo.InvariantCulture)}:{sourceGeneration.ToString(CultureInfo.InvariantCulture)}:compatibility:{PreparationRecipes.WmvVersion}/{PreparationRecipes.TsAacVersion}/{PreparationRecipes.RemuxVersion}";

    public static string OutputFileNameFor(string primaryRelativePath, string fingerprint) =>
        $"{Path.GetFileNameWithoutExtension(primaryRelativePath)}.tvp-{fingerprint[..12]}.mp4";

    public static string ManifestFileNameFor(string outputFileName) => $"{outputFileName}.tvp.json";

    public bool IsManagedOutputName(string name) =>
        name.EndsWith(".tvp.json", StringComparison.OrdinalIgnoreCase)
        || (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && name.Contains(".tvp-", StringComparison.Ordinal));

    public async Task<PreparationOutcome> ExecuteAsync(long jobId, CancellationToken cancellationToken)
    {
        var job = await context.PreparationJobs
            .Include(candidate => candidate.Lesson)
            .ThenInclude(lesson => lesson.SourceComponents)
            .SingleAsync(candidate => candidate.Id == jobId, cancellationToken);

        var attempt = await RecordAttemptStartAsync(job, cancellationToken);

        try
        {
            var components = job.Lesson.SourceComponents
                .Where(component => component.Generation == job.SourceGeneration)
                .ToList();
            var video = components.FirstOrDefault(component => component.Role == SourceComponentRole.Video);
            var companion = components.FirstOrDefault(component => component.Role == SourceComponentRole.CompanionAudio);

            if (video is null)
            {
                return await FailAsync(job, attempt, "missing-source", "The lesson has no current source video.", permanent: true, cancellationToken);
            }

            if (!LibraryPathGuard.TryResolveWithin(appOptions.Value.LibraryRoot, video.RelativePath, out var videoFullPath)
                || !File.Exists(videoFullPath))
            {
                return await BlockAsync(job, attempt, "source-unavailable", "The source video is currently unavailable.", cancellationToken);
            }

            string? companionFullPath = null;
            if (companion is not null
                && !LibraryPathGuard.TryResolveWithin(appOptions.Value.LibraryRoot, companion.RelativePath, out companionFullPath))
            {
                return await BlockAsync(job, attempt, "companion-unavailable", "The companion audio file is currently unavailable.", cancellationToken);
            }

            var probe = LibraryScanner.ParseProbeMetadata(video.ProbeMetadata);
            var sourceSet = new SourceSetInfo(video.RelativePath, videoFullPath, companion?.RelativePath, companionFullPath, probe);
            var recipe = PreparationRecipes.Choose(sourceSet, out var blockedReason);
            if (recipe is null)
            {
                return await FailAsync(
                    job,
                    attempt,
                    blockedReason ?? "unsupported-source",
                    "The source format has no supported preparation recipe in this version.",
                    permanent: true,
                    cancellationToken);
            }

            var fingerprint = SourceFingerprint.Compute(
            [
                ("video", video.RelativePath, videoFullPath),
                .. companion is not null && companionFullPath is not null
                    ? new[] { ("companion-audio", companion.RelativePath, companionFullPath) }
                    : Array.Empty<(string, string, string)>()
            ]);

            if (job.State == PreparationJobState.Queued)
            {
                await TransitionAsync(job, PreparationJobState.Running, cancellationToken);
            }

            var outputDirectory = Path.GetDirectoryName(videoFullPath)!;
            var outputFileName = OutputFileNameFor(video.RelativePath, fingerprint);
            var outputFullPath = Path.Combine(outputDirectory, outputFileName);
            var manifestFullPath = Path.Combine(outputDirectory, ManifestFileNameFor(outputFileName));
            var temporaryFullPath = Path.Combine(outputDirectory, $".{outputFileName}.{job.Id.ToString(CultureInfo.InvariantCulture)}.part");

            if (File.Exists(outputFullPath))
            {
                // A previous attempt already published a byte-identical output for this
                // source set; adopt it instead of encoding again.
                return await AdoptExistingOutputAsync(job, attempt, outputFullPath, manifestFullPath, outputFileName, fingerprint, sourceDurationMs: probe?.DurationMs, cancellationToken);
            }

            if (!CheckDiskReserve(outputDirectory, out var freeMessage))
            {
                return await BlockAsync(job, attempt, "insufficient-disk", freeMessage, cancellationToken);
            }

            job.OutputRelativePath = RelativeToLibraryRoot(outputFullPath);
            job.ManifestRelativePath = RelativeToLibraryRoot(manifestFullPath);

            var arguments = recipe.Arguments
                .Select(argument => argument == "{output}" ? temporaryFullPath : argument)
                .ToList();

            var sourceDuration = probe?.DurationMs;
            await context.SaveChangesAsync(cancellationToken);

            var result = await ffmpegAdapter.RunAsync(
                arguments,
                seconds =>
                {
                    if (sourceDuration is > 0)
                    {
                        job.Progress = Math.Clamp(seconds * 1000.0 / sourceDuration.Value, 0, 1);
                    }
                },
                cancellationToken);
            await context.SaveChangesAsync(cancellationToken);

            if (!result.Success)
            {
                File.Delete(temporaryFullPath);
                var transient = IsTransient(result.Error);
                return await FailAsync(job, attempt, "encode-failed", result.Error ?? "The encode failed.", permanent: !transient, cancellationToken);
            }

            await TransitionAsync(job, PreparationJobState.Validating, cancellationToken);
            var validation = await validator.ValidateAsync(temporaryFullPath, sourceDuration, "h264", recipe.ExpectedAudioCodec, cancellationToken);
            if (!validation.Success)
            {
                File.Delete(temporaryFullPath);
                return await FailAsync(job, attempt, "invalid-output", validation.Error ?? "The prepared output failed validation.", permanent: false, cancellationToken);
            }

            await TransitionAsync(job, PreparationJobState.Publishing, cancellationToken);

            var manifest = new PreparationManifest
            {
                JobId = job.Id,
                LessonId = job.LessonId,
                SourceGeneration = job.SourceGeneration,
                RecipeVersion = recipe.RecipeVersion,
                Sources = [new ManifestSource("video", video.RelativePath, fingerprint)],
                OutputFileName = outputFileName,
                OutputHash = validation.OutputHash,
                OutputLengthBytes = validation.ByteLength,
                DurationMs = validation.Probe.DurationMs,
                Width = validation.Probe.Width,
                Height = validation.Probe.Height,
                VideoCodec = validation.Probe.VideoCodec,
                AudioCodec = validation.Probe.AudioCodec,
                State = "Prepared"
            };
            manifest.WriteAtomically(manifestFullPath);

            File.Move(temporaryFullPath, outputFullPath, overwrite: false);

            manifest = manifest with { State = "Committed" };
            manifest.WriteAtomically(manifestFullPath);

            await PublishAsync(job, attempt, outputFullPath, manifestFullPath, outputFileName, validation, cancellationToken);
            await CompleteAttemptAsync(attempt, exitCode: 0, failure: null, temporaryFullPath, cancellationToken);
            await TransitionAsync(job, PreparationJobState.Succeeded, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Prepared {Output} for lesson {LessonId}.", outputFileName, job.LessonId);
            return new PreparationOutcome(PreparationJobState.Succeeded, null, null);
        }
        catch (OperationCanceledException)
        {
            await TransitionAsync(job, PreparationJobState.Interrupted, cancellationToken: CancellationToken.None);
            await CompleteAttemptAsync(attempt, null, "Interrupted by shutdown.", null, CancellationToken.None);
            await context.SaveChangesAsync(CancellationToken.None);
            return new PreparationOutcome(PreparationJobState.Interrupted, "interrupted", "Interrupted by shutdown.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Preparation job {JobId} failed unexpectedly.", jobId);
            if (job.State is PreparationJobState.Running or PreparationJobState.Validating or PreparationJobState.Publishing)
            {
                await TransitionAsync(job, PreparationJobState.Interrupted, CancellationToken.None);
                await CompleteAttemptAsync(attempt, null, $"Unexpected failure: {exception.GetType().Name}.", null, CancellationToken.None);
                if (job.Attempt < MaxAutomaticAttempts)
                {
                    await TransitionAsync(job, PreparationJobState.Queued, CancellationToken.None);
                    await context.SaveChangesAsync(CancellationToken.None);
                }
                else
                {
                    await TransitionAsync(job, PreparationJobState.Failed, CancellationToken.None);
                    job.ErrorCode = "unexpected-error";
                    job.UserMessage = "The preparation failed unexpectedly. Retry from the queue.";
                    await context.SaveChangesAsync(CancellationToken.None);
                }
            }

            return new PreparationOutcome(job.State, job.ErrorCode, job.UserMessage);
        }
    }

    public async Task RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        // The install lock guarantees a single process, so any in-flight state at
        // boot belongs to a dead process and is recovered unconditionally.
        var staleJobs = await context.PreparationJobs
            .Where(job => job.State == PreparationJobState.Interrupted
                || job.State == PreparationJobState.Running
                || job.State == PreparationJobState.Validating
                || job.State == PreparationJobState.Publishing)
            .Include(job => job.Lesson)
            .ThenInclude(lesson => lesson.SourceComponents)
            .ToListAsync(cancellationToken);

        foreach (var job in staleJobs)
        {
            var video = job.Lesson.SourceComponents.FirstOrDefault(component =>
                component.Generation == job.SourceGeneration && component.Role == SourceComponentRole.Video);
            if (video is null)
            {
                await RequeueAsync(job, cancellationToken);
                continue;
            }

            if (!LibraryPathGuard.TryResolveWithin(appOptions.Value.LibraryRoot, video.RelativePath, out var videoFullPath))
            {
                await RequeueAsync(job, cancellationToken);
                continue;
            }

            var outputDirectory = Path.GetDirectoryName(videoFullPath)!;
            var manifestPath = job.ManifestRelativePath is null
                ? null
                : Path.Combine(appOptions.Value.LibraryRoot, job.ManifestRelativePath);
            var outputPath = job.OutputRelativePath is null
                ? null
                : Path.Combine(appOptions.Value.LibraryRoot, job.OutputRelativePath);
            var temporary = Directory.EnumerateFiles(outputDirectory, $".*.tvp-*.mp4.{job.Id.ToString(CultureInfo.InvariantCulture)}.part")
                .FirstOrDefault();

            var manifest = manifestPath is not null ? PreparationManifest.TryRead(manifestPath) : null;

            if (outputPath is not null && File.Exists(outputPath) && manifest?.State == "Committed")
            {
                // A completed publication whose database row never landed: adopt it.
                var validation = await validator.ValidateAsync(
                    outputPath, job.Lesson.DurationMs, manifest.VideoCodec, manifest.AudioCodec, cancellationToken);
                if (validation.Success)
                {
                    await PublishAsync(job, null, outputPath, manifestPath!, manifest.OutputFileName, validation, cancellationToken);
                    await TransitionAsync(job, PreparationJobState.Succeeded, cancellationToken);
                    await context.SaveChangesAsync(cancellationToken);
                    logger.LogInformation("Recovered committed output for job {JobId}.", job.Id);
                    continue;
                }
            }

            if (manifest?.State == "Prepared" && outputPath is not null && File.Exists(outputPath))
            {
                // Prepared manifest with the final file present: finish publication.
                var validation = await validator.ValidateAsync(
                    outputPath, job.Lesson.DurationMs, manifest.VideoCodec, manifest.AudioCodec, cancellationToken);
                if (validation.Success)
                {
                    manifest = manifest with { State = "Committed" };
                    manifest.WriteAtomically(manifestPath!);
                    await PublishAsync(job, null, outputPath, manifestPath!, manifest.OutputFileName, validation, cancellationToken);
                    await TransitionAsync(job, PreparationJobState.Succeeded, cancellationToken);
                    await context.SaveChangesAsync(cancellationToken);
                    logger.LogInformation("Recovered prepared output for job {JobId}.", job.Id);
                    continue;
                }
            }

            if (temporary is not null)
            {
                // A partial encode is never a rendition; drop it and requeue.
                File.Delete(temporary);
            }

            await RequeueAsync(job, cancellationToken);
        }
    }

    private async Task PublishAsync(
        PreparationJobEntity job,
        PreparationAttemptEntity? attempt,
        string outputFullPath,
        string manifestFullPath,
        string outputFileName,
        OutputValidation validation,
        CancellationToken cancellationToken)
    {
        var rendition = await context.Renditions.SingleOrDefaultAsync(candidate =>
            candidate.LessonId == job.LessonId
            && candidate.SourceGeneration == job.SourceGeneration
            && candidate.Purpose == RenditionPurpose.Compatibility, cancellationToken);

        if (rendition is null)
        {
            rendition = new RenditionEntity
            {
                LessonId = job.LessonId,
                SourceGeneration = job.SourceGeneration,
                Purpose = RenditionPurpose.Compatibility,
                RetentionClass = RenditionRetention.Permanent,
                RelativePath = RelativeToLibraryRoot(outputFullPath),
                ManifestPath = RelativeToLibraryRoot(manifestFullPath)
            };
            context.Renditions.Add(rendition);
        }

        rendition.Status = RenditionStatus.Ready;
        rendition.RecipeVersion = job.RecipeVersion;
        rendition.Profile = null;
        rendition.RelativePath = RelativeToLibraryRoot(outputFullPath);
        rendition.ManifestPath = RelativeToLibraryRoot(manifestFullPath);
        rendition.Width = validation.Probe.Width;
        rendition.Height = validation.Probe.Height;
        rendition.VideoCodec = validation.Probe.VideoCodec;
        rendition.AudioCodec = validation.Probe.AudioCodec;
        rendition.ByteLength = validation.ByteLength;
        rendition.OutputHash = validation.OutputHash;
        rendition.LastAccessUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        rendition.Revision++;

        job.OutputRelativePath = rendition.RelativePath;
        job.ManifestRelativePath = rendition.ManifestPath;

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task TransitionAsync(PreparationJobEntity job, PreparationJobState target, CancellationToken cancellationToken = default)
    {
        if (!PreparationTransitions.CanTransition(job.State, target))
        {
            throw new InvalidOperationException($"A preparation job cannot move from {job.State} to {target}.");
        }

        job.State = target;
        if (target == PreparationJobState.Running)
        {
            job.LeaseOwner = Environment.MachineName + ":" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            job.LeaseExpiresUtcMs = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds();
        }

        job.Revision++;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<PreparationAttemptEntity> RecordAttemptStartAsync(PreparationJobEntity job, CancellationToken cancellationToken)
    {
        job.StartedUtcMs ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var attempt = new PreparationAttemptEntity
        {
            Job = job,
            Attempt = job.Attempt,
            StartedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        context.PreparationAttempts.Add(attempt);
        await context.SaveChangesAsync(cancellationToken);
        return attempt;
    }

    private async Task CompleteAttemptAsync(
        PreparationAttemptEntity attempt,
        int? exitCode,
        string? failure,
        string? tempRelativePath,
        CancellationToken cancellationToken)
    {
        attempt.FinishedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        attempt.ExitCode = exitCode;
        attempt.SanitizedFailure = failure;
        attempt.TempRelativePath = tempRelativePath;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<PreparationOutcome> BlockAsync(
        PreparationJobEntity job,
        PreparationAttemptEntity attempt,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        await TransitionAsync(job, PreparationJobState.Blocked, cancellationToken);
        job.ErrorCode = code;
        job.UserMessage = message;
        await CompleteAttemptAsync(attempt, null, message, null, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return new PreparationOutcome(PreparationJobState.Blocked, code, message);
    }

    private async Task<PreparationOutcome> FailAsync(
        PreparationJobEntity job,
        PreparationAttemptEntity attempt,
        string code,
        string message,
        bool permanent,
        CancellationToken cancellationToken)
    {
        if (!permanent && job.Attempt < MaxAutomaticAttempts)
        {
            await CompleteAttemptAsync(attempt, null, message, null, cancellationToken);
            await TransitionAsync(job, PreparationJobState.Queued, cancellationToken);
            job.ErrorCode = code;
            job.UserMessage = message;
            await context.SaveChangesAsync(cancellationToken);
            return new PreparationOutcome(PreparationJobState.Queued, code, message);
        }

        await TransitionAsync(job, PreparationJobState.Failed, cancellationToken);
        job.ErrorCode = code;
        job.UserMessage = message;
        await CompleteAttemptAsync(attempt, null, message, null, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return new PreparationOutcome(PreparationJobState.Failed, code, message);
    }

    private async Task RequeueAsync(PreparationJobEntity job, CancellationToken cancellationToken)
    {
        if (job.State != PreparationJobState.Queued)
        {
            await TransitionAsync(job, PreparationJobState.Queued, cancellationToken);
        }

        job.LeaseOwner = null;
        job.LeaseExpiresUtcMs = null;
        job.Revision++;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<PreparationOutcome> AdoptExistingOutputAsync(
        PreparationJobEntity job,
        PreparationAttemptEntity attempt,
        string outputFullPath,
        string manifestFullPath,
        string outputFileName,
        string fingerprint,
        long? sourceDurationMs,
        CancellationToken cancellationToken)
    {
        var manifest = PreparationManifest.TryRead(manifestFullPath);
        var validation = await validator.ValidateAsync(outputFullPath, sourceDurationMs, manifest?.VideoCodec, manifest?.AudioCodec, cancellationToken);
        if (validation.Success)
        {
            if (manifest is null || manifest.State != "Committed")
            {
                var committed = (manifest ?? new PreparationManifest
                    {
                        JobId = job.Id,
                        LessonId = job.LessonId,
                        SourceGeneration = job.SourceGeneration,
                        OutputFileName = outputFileName
                    }) with { State = "Committed", OutputHash = validation.OutputHash, OutputLengthBytes = validation.ByteLength };
                committed.WriteAtomically(manifestFullPath);
            }

            await CompleteAttemptAsync(attempt, 0, null, null, cancellationToken);
            await PublishAsync(job, attempt, outputFullPath, manifestFullPath, outputFileName, validation, cancellationToken);
            await TransitionAsync(job, PreparationJobState.Succeeded, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            return new PreparationOutcome(PreparationJobState.Succeeded, null, null);
        }

        // The existing file is not a valid current output; quarantine nothing and
        // report failure so a human can inspect it. We never overwrite it blindly.
        File.Delete(manifestFullPath);
        return await FailAsync(
            job,
            attempt,
            "existing-output-invalid",
            "A file with the prepared output name already exists but is not a valid current copy.",
            permanent: true,
            cancellationToken);
    }

    private bool CheckDiskReserve(string outputDirectory, out string message)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(outputDirectory)) ?? "/";
        var drive = DriveInfo.GetDrives().FirstOrDefault(candidate =>
            string.Equals(candidate.Name.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        if (drive is not null && drive.AvailableFreeSpace < preparationOptions.Value.DiskReserveBytes)
        {
            message = $"Not enough free space to prepare this version. {drive.AvailableFreeSpace / 1_000_000_000.0:0.#} GB free, {preparationOptions.Value.DiskReserveBytes / 1_000_000_000.0:0.#} GB required.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private string RelativeToLibraryRoot(string fullPath) =>
        Path.GetRelativePath(appOptions.Value.LibraryRoot, fullPath).Replace('\\', '/');

    private static bool IsTransient(string? error) =>
        error is not null
        && (error.Contains("I/O error", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No space left", StringComparison.OrdinalIgnoreCase)
            || error.Contains("could not be started", StringComparison.OrdinalIgnoreCase));
}