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
    CacheAccounting cacheAccounting,
    CacheEvictionService evictionService,
    ILogger<PreparationExecutor> logger)
{
    private const int MaxAutomaticAttempts = 3;

    public static string OutputFileNameFor(string primaryRelativePath, string fingerprint) =>
        $"{Path.GetFileNameWithoutExtension(primaryRelativePath)}.tvp-{fingerprint[..12]}.mp4";

    public static string ManifestFileNameFor(string outputFileName) => $"{outputFileName}.tvp.json";

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

            if (job.Lesson.SourceGeneration != job.SourceGeneration)
            {
                return await FailAsync(job, attempt, "source-changed", "The source generation changed; use the current lesson's preparation.", true, cancellationToken);
            }

            if (job.Purpose == RenditionPurpose.Quality)
            {
                return await ExecuteQualityAsync(
                    job,
                    attempt,
                    videoFullPath,
                    video,
                    companionFullPath,
                    companion,
                    cancellationToken);
            }

            var inspected = await probeAdapter.ProbeAsync(videoFullPath, cancellationToken);
            var probe = inspected.Success
                ? new ProbeMetadata(inspected.DurationMs, inspected.VideoCodec, inspected.AudioCodec, inspected.Width, inspected.Height)
                : LibraryScanner.ParseProbeMetadata(video.ProbeMetadata);
            var sourceSet = new SourceSetInfo(video.RelativePath, videoFullPath, companion?.RelativePath, companionFullPath, probe);
            var recipe = PreparationRecipes.Choose(sourceSet, out var blockedReason);
            if (recipe is null)
            {
                return await BlockAsync(job, attempt, "unsupported-source",
                    "The source format has no supported preparation recipe in this version.", cancellationToken);
            }

            job.RecipeVersion = recipe.RecipeVersion;
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

            if (File.Exists(temporaryFullPath))
            {
                return await BlockAsync(job, attempt, "temporary-file-exists", "A preparation temporary file already exists. Restart to recover it safely.", cancellationToken);
            }
            if (File.Exists(manifestFullPath))
            {
                var previous = PreparationManifest.TryRead(manifestFullPath);
                if (previous is null || previous.JobId != job.Id || previous.Sources.Count == 0 || previous.Sources[0].Fingerprint != fingerprint)
                    return await BlockAsync(job, attempt, "unverified-manifest", "An unrelated sidecar occupies the output path. It has been preserved.", cancellationToken);
            }
            attempt.TempRelativePath = RelativeToLibraryRoot(temporaryFullPath);
            await context.SaveChangesAsync(cancellationToken);
            // Reserve a new temporary name; FFmpeg may overwrite only this owned empty file.
            using (new FileStream(temporaryFullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            var arguments = recipe.Arguments
                .Select(argument => argument == "{output}" ? temporaryFullPath : argument)
                .ToList();

            var sourceDuration = probe?.DurationMs;
            var lastProgressSave = Stopwatch.StartNew();
            var result = await ffmpegAdapter.RunAsync(
                arguments,
                async seconds =>
                {
                    if (lastProgressSave.Elapsed >= TimeSpan.FromSeconds(2) && !CheckDiskReserve(outputDirectory, out var reserveMessage))
                        throw new IOException(reserveMessage);
                    if (sourceDuration is > 0)
                    {
                        job.Progress = Math.Clamp(seconds * 1000.0 / sourceDuration.Value, 0, 1);
                        if (lastProgressSave.Elapsed >= TimeSpan.FromSeconds(2))
                        {
                            job.LeaseExpiresUtcMs = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds();
                            await context.SaveChangesAsync(cancellationToken);
                            lastProgressSave.Restart();
                        }
                    }
                },
                cancellationToken);
            await context.SaveChangesAsync(cancellationToken);

            if (!result.Success)
            {
                File.Delete(temporaryFullPath);
                if (result.Error?.Contains("No space left", StringComparison.OrdinalIgnoreCase) == true
                    || result.Error?.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) == true)
                    return await BlockAsync(job, attempt, "output-unavailable", "The output volume is full or not writable. Check storage and retry.", cancellationToken);
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

            var currentFingerprint = SourceFingerprint.Compute(
                [("video", video.RelativePath, videoFullPath),
                 .. companion is not null && companionFullPath is not null
                    ? new[] { ("companion-audio", companion.RelativePath, companionFullPath) }
                    : Array.Empty<(string, string, string)>()]);
            if (currentFingerprint != fingerprint)
            {
                File.Delete(temporaryFullPath);
                return await FailAsync(job, attempt, "source-changed", "Source files changed during preparation. Refresh the library.", true, cancellationToken);
            }
            await TransitionAsync(job, PreparationJobState.Publishing, cancellationToken);

            var manifest = new PreparationManifest
            {
                JobId = job.Id,
                LessonId = job.LessonId,
                SourceGeneration = job.SourceGeneration,
                RecipeVersion = recipe.RecipeVersion,
                LibraryLogicalIdentity = await context.Lessons.Where(l => l.Id == job.LessonId).Select(l => l.Course.LibraryId).Join(context.Libraries, id => id, l => l.Id, (id, l) => l.LogicalIdentity).SingleAsync(cancellationToken),
                Sources = [new ManifestSource("video", video.RelativePath, fingerprint),
                    .. companion is not null ? new[] { new ManifestSource("companion-audio", companion.RelativePath, fingerprint) } : Array.Empty<ManifestSource>()],
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
                if (attempt.TempRelativePath is not null && LibraryPathGuard.TryResolveWithin(appOptions.Value.LibraryRoot, attempt.TempRelativePath, out var interruptedTemp))
                    File.Delete(interruptedTemp);
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

    /// <summary>
    /// What the eligibility checks established about a quality job: the profile is known, the
    /// source really was measured, and the requested height is genuinely below the source's.
    /// Everything downstream reads dimensions and duration from here rather than re-probing.
    /// </summary>
    private sealed record QualityPlan(
        string Profile,
        ProbeMetadata SourceProbe,
        int SourceWidth,
        int SourceHeight,
        int ScaledWidth,
        int ScaledHeight)
    {
        /// <summary>How this version is named to the reader, e.g. "720p".</summary>
        public string Label => QualityProfiles.LabelFor(Profile, SourceWidth, SourceHeight);
    }

    /// <summary>
    /// The encoder invocation plus the three paths the publication protocol moves between:
    /// the temporary file being written, the output it is renamed to, and the manifest that
    /// records the rename.
    /// </summary>
    private sealed record QualityRecipe(
        IReadOnlyList<string> Arguments,
        string? ExpectedAudioCodec,
        string Fingerprint,
        string VideoRelativePath,
        string OutputDirectory,
        string OutputFileName,
        string OutputFullPath,
        string ManifestFullPath,
        string TemporaryFullPath);

    /// <summary>
    /// How the encode ended, and the budget it was held to. <paramref name="Headroom" /> is
    /// the cache space the temporary file was allowed to occupy while running.
    /// </summary>
    private sealed record QualityEncodeResult(
        FFmpegResult Result,
        bool OverBudget,
        long Headroom,
        long CacheLimit);

    /// <summary>
    /// Prepares a lower-quality rendition: decide eligibility, adopt any copy that is already
    /// there, reserve cache space, encode, settle the result against the budget, publish.
    /// Each step either returns a terminal outcome or hands the next one what it needs.
    /// </summary>
    private async Task<PreparationOutcome> ExecuteQualityAsync(
        PreparationJobEntity job,
        PreparationAttemptEntity attempt,
        string videoFullPath,
        SourceComponentEntity video,
        string? companionFullPath,
        SourceComponentEntity? companion,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveQualityPlanAsync(job, attempt, videoFullPath, cancellationToken);
        if (resolved.Plan is not { } plan)
        {
            // Rejected is always set when Plan is not: the job has been failed with a reason.
            return resolved.Rejected!;
        }

        var adopted = await TryAdoptReadyQualityRenditionAsync(job, attempt, plan, cancellationToken);
        if (adopted is not null)
        {
            return adopted;
        }

        var (cacheLimit, blocked) = await ReserveCacheBudgetAsync(job, attempt, plan, videoFullPath, cancellationToken);
        if (blocked is not null)
        {
            return blocked;
        }

        var recipe = BuildQualityRecipe(job, plan, videoFullPath, video, companionFullPath, companion);
        Directory.CreateDirectory(recipe.OutputDirectory);
        if (!CheckDiskReserve(recipe.OutputDirectory, out var freeMessage))
        {
            return await BlockAsync(job, attempt, "insufficient-disk", freeMessage, cancellationToken);
        }

        job.OutputRelativePath = RelativeToLibraryRoot(recipe.OutputFullPath);
        job.ManifestRelativePath = RelativeToLibraryRoot(recipe.ManifestFullPath);

        // Recorded before the encoder starts: a crash mid-encode has to leave a trail to
        // the partial file, which lives in the managed quality area rather than beside the
        // source, so recovery can delete it instead of guessing.
        attempt.TempRelativePath = RelativeToLibraryRoot(recipe.TemporaryFullPath);
        await context.SaveChangesAsync(cancellationToken);

        var encode = await RunQualityEncodeAsync(job, recipe, plan.SourceProbe.DurationMs, cacheLimit, cancellationToken);

        var settled = await SettleQualityEncodeAsync(job, attempt, plan, recipe, encode, cancellationToken);
        if (settled.Validation is not { } validation)
        {
            // Stopped is always set when Validation is not.
            return settled.Stopped!;
        }

        return await PublishQualityOutputAsync(job, attempt, plan, recipe, validation, cacheLimit, cancellationToken);
    }

    /// <summary>
    /// Establishes that this job can produce something worth having. An unknown profile, an
    /// unmeasurable source, or a target that is not below the source height are all permanent
    /// failures — retrying them would reach the same answer.
    /// </summary>
    private async Task<(QualityPlan? Plan, PreparationOutcome? Rejected)> ResolveQualityPlanAsync(
        PreparationJobEntity job,
        PreparationAttemptEntity attempt,
        string videoFullPath,
        CancellationToken cancellationToken)
    {
        var profile = job.Profile;
        if (!QualityProfiles.IsKnown(profile))
        {
            return (null, await FailAsync(job, attempt, "unknown-profile", "The requested quality profile is not known.", permanent: true, cancellationToken));
        }

        // Quality work always measures the real source: probe fresh rather than
        // trusting cached dimensions, which the compatibility path may not have
        // gathered before the dispatch.
        var qualityProbe = await probeAdapter.ProbeAsync(videoFullPath, cancellationToken);
        if (!qualityProbe.Success)
        {
            return (null, await FailAsync(job, attempt, "probe-failed", qualityProbe.Error ?? "The source could not be probed.", permanent: true, cancellationToken));
        }

        var sourceWidth = qualityProbe.Width;
        var sourceHeight = qualityProbe.Height;
        if (sourceWidth is not > 0 || sourceHeight is not > 0)
        {
            return (null, await FailAsync(job, attempt, "probe-incomplete", "The source dimensions are unknown, so no quality version can be prepared.", permanent: true, cancellationToken));
        }

        var targetHeight = QualityProfiles.TargetHeight(profile)!.Value;
        if (targetHeight >= sourceHeight.Value)
        {
            // The native rendition already serves this quality; upscaling is never offered.
            return (null, await FailAsync(
                job,
                attempt,
                "profile-eligibility",
                $"The requested {profile}p quality is not below the source height ({sourceHeight}p); the original already provides this quality.",
                permanent: true,
                cancellationToken));
        }

        // The quality branch is dispatched with no cached probe, so everything downstream —
        // progress, the audio decision and duration validation — has to use this one.
        var sourceProbe = new ProbeMetadata(
            qualityProbe.DurationMs, qualityProbe.VideoCodec, qualityProbe.AudioCodec, qualityProbe.Width, qualityProbe.Height);
        var (scaledWidth, scaledHeight) = QualityProfiles.ScaledDimensions(sourceWidth.Value, sourceHeight.Value, targetHeight);

        return (new QualityPlan(profile!, sourceProbe, sourceWidth.Value, sourceHeight.Value, scaledWidth, scaledHeight), null);
    }

    /// <summary>
    /// An existing rendition for this profile means the work is either already done or its
    /// file has vanished — dedup happened at scheduling time. A copy that still validates is
    /// adopted and the job succeeds without encoding; anything else returns null so the
    /// caller prepares fresh.
    /// </summary>
    private async Task<PreparationOutcome?> TryAdoptReadyQualityRenditionAsync(
        PreparationJobEntity job,
        PreparationAttemptEntity attempt,
        QualityPlan plan,
        CancellationToken cancellationToken)
    {
        var profile = plan.Profile;
        var existing = await context.Renditions
            .SingleOrDefaultAsync(candidate => candidate.LessonId == job.LessonId
                && candidate.SourceGeneration == job.SourceGeneration
                && candidate.Purpose == RenditionPurpose.Quality
                && candidate.Profile == profile, cancellationToken);
        if (existing is null
            || existing.Status != RenditionStatus.Ready
            || !LibraryPathGuard.TryResolveWithin(appOptions.Value.LibraryRoot, existing.RelativePath, out var existingFullPath))
        {
            return null;
        }

        var existingValidation = await validator.ValidateAsync(existingFullPath, plan.SourceProbe.DurationMs, "h264", existing.AudioCodec, cancellationToken);
        if (!existingValidation.Success)
        {
            return null;
        }

        await CompleteAttemptAsync(attempt, 0, null, null, cancellationToken);
        await TransitionAsync(job, PreparationJobState.Succeeded, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return new PreparationOutcome(PreparationJobState.Succeeded, null, null);
    }

    /// <summary>
    /// Cache budget: ready plus reserved bytes must leave room for this job's reservation.
    /// Least-recently-watched quality copies are evicted to make room, and the job is blocked
    /// when even that is not enough. On success the reservation is recorded against the job
    /// so concurrent jobs see the space as spoken for.
    /// </summary>
    private async Task<(long CacheLimit, PreparationOutcome? Blocked)> ReserveCacheBudgetAsync(
        PreparationJobEntity job,
        PreparationAttemptEntity attempt,
        QualityPlan plan,
        string videoFullPath,
        CancellationToken cancellationToken)
    {
        var cacheLimit = await cacheAccounting.GetCacheLimitAsync(cancellationToken);
        var reservation = new FileInfo(videoFullPath).Length;
        var usage = await cacheAccounting.GetChargeableQualityBytesAsync(cancellationToken)
            + await cacheAccounting.GetReservedQualityBytesAsync(cancellationToken);
        if (usage + reservation > cacheLimit)
        {
            await evictionService.EvictUntilUnderLimitAsync(cacheLimit - reservation, cancellationToken);
            usage = await cacheAccounting.GetChargeableQualityBytesAsync(cancellationToken)
                + await cacheAccounting.GetReservedQualityBytesAsync(cancellationToken);
            if (usage + reservation > cacheLimit)
            {
                return (cacheLimit, await BlockAsync(
                    job,
                    attempt,
                    "cache-full",
                    $"Not enough quality cache space to prepare this version. The {plan.Label} version needs about {cacheAccounting.DescribeBytes(reservation)}; the cache limit is {cacheAccounting.DescribeBytes(cacheLimit)}. Reduce the limit's contents or raise the limit in settings.",
                    cancellationToken));
            }
        }

        job.ReservedBytes = reservation;
        await context.SaveChangesAsync(cancellationToken);
        return (cacheLimit, null);
    }

    /// <summary>
    /// Turns the plan into an encoder invocation and the paths it writes. Pure: no database,
    /// no filesystem, so the recipe can be reasoned about (and the directory created) before
    /// anything is committed.
    /// </summary>
    private QualityRecipe BuildQualityRecipe(
        PreparationJobEntity job,
        QualityPlan plan,
        string videoFullPath,
        SourceComponentEntity video,
        string? companionFullPath,
        SourceComponentEntity? companion)
    {
        // A companion carries the audio when one exists; otherwise the source's own track,
        // which may legitimately be absent. Audio is only required of the output when the
        // input actually has some, so a silent source is not failed for missing AAC.
        var incomingAudioCodec = companionFullPath is not null ? "aac" : plan.SourceProbe.AudioCodec;
        var hasAudio = incomingAudioCodec is not null;
        var needsAudioTranscode = hasAudio && incomingAudioCodec != "aac";
        var expectedAudioCodec = !hasAudio ? null : needsAudioTranscode ? "aac" : incomingAudioCodec;

        var fingerprint = SourceFingerprint.Compute(
        [
            ("video", video.RelativePath, videoFullPath),
            .. companion is not null && companionFullPath is not null
                ? new[] { ("companion-audio", companion.RelativePath, companionFullPath) }
                : Array.Empty<(string, string, string)>()
        ]);

        var outputDirectory = Path.Combine(appOptions.Value.LibraryRoot, ".tutsvideoplayer", "quality",
            job.LessonId.ToString(CultureInfo.InvariantCulture), job.SourceGeneration.ToString(CultureInfo.InvariantCulture));
        var outputFileName = $"{SanitizeStem(video.RelativePath)}-{plan.Profile.ToLowerInvariant()}-{fingerprint[..12]}.mp4";
        var outputFullPath = Path.Combine(outputDirectory, outputFileName);
        var temporaryFullPath = Path.Combine(outputDirectory, $".{outputFileName}.{job.Id.ToString(CultureInfo.InvariantCulture)}.part");

        // The encoder writes straight to the temporary file; the rename to outputFullPath is
        // what publishes it, so the encoder never names the final path.
        var arguments = new List<string> { "-i", videoFullPath };
        arguments.AddRange(companionFullPath is not null
            ? new[] { "-i", companionFullPath, "-map", "0:v:0", "-map", "1:a:0" }
            : new[] { "-map", "0:v:0", "-map", "0:a:0?" });
        arguments.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p"]);
        arguments.AddRange(needsAudioTranscode || !hasAudio
            ? new[] { "-c:a", "aac", "-b:a", "192k" }
            : new[] { "-c:a", "copy" });
        arguments.AddRange(
        [
            "-vf", $"scale={plan.ScaledWidth.ToString(CultureInfo.InvariantCulture)}:{plan.ScaledHeight.ToString(CultureInfo.InvariantCulture)}",
            "-movflags", "+faststart",
            "-f", "mp4", "-y", temporaryFullPath
        ]);

        return new QualityRecipe(
            arguments,
            expectedAudioCodec,
            fingerprint,
            video.RelativePath,
            outputDirectory,
            outputFileName,
            outputFullPath,
            outputFullPath + ".tvp.json",
            temporaryFullPath);
    }

    /// <summary>
    /// Runs the encoder, reporting progress onto the job and watching the growing temporary
    /// file against the cache headroom. Exceeding the headroom cancels the encode there and
    /// then rather than letting it finish and relying on eviction to clean up afterwards.
    /// </summary>
    private async Task<QualityEncodeResult> RunQualityEncodeAsync(
        PreparationJobEntity job,
        QualityRecipe recipe,
        long? sourceDurationMs,
        long cacheLimit,
        CancellationToken cancellationToken)
    {
        // Everything already charged to the cache that this encode is not producing: stored
        // quality copies plus the reservations of other in-flight jobs. The growing
        // temporary file has to fit in what is left, not merely stay under some multiple of
        // its own estimate.
        var committedBytes = await cacheAccounting.GetChargeableQualityBytesAsync(cancellationToken)
            + await cacheAccounting.GetReservedQualityBytesAsync(job.Id, cancellationToken);
        var headroom = cacheLimit - committedBytes;

        var overBudget = false;
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        FFmpegResult result;
        try
        {
            result = await ffmpegAdapter.RunAsync(
                recipe.Arguments,
                async seconds =>
                {
                    if (sourceDurationMs is > 0)
                    {
                        job.Progress = Math.Clamp(seconds * 1000.0 / sourceDurationMs.Value, 0, 1);
                    }

                    if (!overBudget && File.Exists(recipe.TemporaryFullPath))
                    {
                        var current = new FileInfo(recipe.TemporaryFullPath).Length;
                        if (current > headroom)
                        {
                            // Stop the encoder now rather than letting it run to completion
                            // and relying on eviction to clean up afterwards.
                            overBudget = true;
                            budgetCts.Cancel();
                        }
                    }

                    await Task.CompletedTask;
                },
                budgetCts.Token);
        }
        catch (OperationCanceledException) when (overBudget && !cancellationToken.IsCancellationRequested)
        {
            result = new FFmpegResult(null, Completed: false, "Stopped because the quality cache budget was reached.", null);
        }

        await context.SaveChangesAsync(cancellationToken);
        return new QualityEncodeResult(result, overBudget, headroom, cacheLimit);
    }

    /// <summary>
    /// Decides whether the encode left something publishable: a failed or over-budget run is
    /// discarded, a finished file that no longer fits gets one last eviction pass before
    /// being given up on, and what survives is validated against the source. Returns the
    /// validation on success, or the terminal outcome that ended the job.
    /// </summary>
    private async Task<(OutputValidation? Validation, PreparationOutcome? Stopped)> SettleQualityEncodeAsync(
        PreparationJobEntity job,
        PreparationAttemptEntity attempt,
        QualityPlan plan,
        QualityRecipe recipe,
        QualityEncodeResult encode,
        CancellationToken cancellationToken)
    {
        var cacheLimit = encode.CacheLimit;

        if (!encode.Result.Success || encode.OverBudget)
        {
            TryDeleteTemporary(recipe.TemporaryFullPath);
            job.ReservedBytes = null;
            if (encode.OverBudget)
            {
                return (null, await BlockAsync(
                    job,
                    attempt,
                    "cache-full",
                    $"The {plan.Label} version did not fit in the remaining quality cache ({cacheAccounting.DescribeBytes(Math.Max(0, encode.Headroom))} free of a {cacheAccounting.DescribeBytes(cacheLimit)} limit) and was discarded. Raise the limit in settings or remove other prepared versions.",
                    cancellationToken));
            }

            var transient = IsTransient(encode.Result.Error);
            return (null, await FailAsync(job, attempt, "encode-failed", encode.Result.Error ?? "The encode failed.", permanent: !transient, cancellationToken));
        }

        var tempInfo = new FileInfo(recipe.TemporaryFullPath);
        if (tempInfo.Length > encode.Headroom)
        {
            // One last chance to make room from cold copies before giving up on the encode.
            await evictionService.EvictUntilUnderLimitAsync(Math.Max(0, cacheLimit - tempInfo.Length), cancellationToken);
            var committedBytes = await cacheAccounting.GetChargeableQualityBytesAsync(cancellationToken)
                + await cacheAccounting.GetReservedQualityBytesAsync(job.Id, cancellationToken);
            if (committedBytes + tempInfo.Length > cacheLimit)
            {
                TryDeleteTemporary(recipe.TemporaryFullPath);
                job.ReservedBytes = null;
                return (null, await BlockAsync(
                    job,
                    attempt,
                    "cache-full",
                    $"The prepared {plan.Label} version does not fit in the {cacheAccounting.DescribeBytes(cacheLimit)} quality cache and was discarded.",
                    cancellationToken));
            }
        }

        await TransitionAsync(job, PreparationJobState.Validating, cancellationToken);
        var validation = await validator.ValidateAsync(recipe.TemporaryFullPath, plan.SourceProbe.DurationMs, "h264", recipe.ExpectedAudioCodec, cancellationToken);
        if (!validation.Success)
        {
            TryDeleteTemporary(recipe.TemporaryFullPath);
            job.ReservedBytes = null;
            return (null, await FailAsync(job, attempt, "invalid-output", validation.Error ?? "The prepared output failed validation.", permanent: false, cancellationToken));
        }

        return (validation, null);
    }

    /// <summary>
    /// Publishes the validated temporary file: Prepared manifest, rename without overwrite,
    /// Committed manifest, then the rendition row. The manifest is written on both sides of
    /// the rename so a crash in between is recognisable during recovery.
    /// </summary>
    private async Task<PreparationOutcome> PublishQualityOutputAsync(
        PreparationJobEntity job,
        PreparationAttemptEntity attempt,
        QualityPlan plan,
        QualityRecipe recipe,
        OutputValidation validation,
        long cacheLimit,
        CancellationToken cancellationToken)
    {
        await TransitionAsync(job, PreparationJobState.Publishing, cancellationToken);

        var manifest = new PreparationManifest
        {
            JobId = job.Id,
            LessonId = job.LessonId,
            SourceGeneration = job.SourceGeneration,
            RecipeVersion = QualityProfiles.RecipeVersionFor(plan.Profile),
            Sources = [new ManifestSource("video", recipe.VideoRelativePath, recipe.Fingerprint)],
            OutputFileName = recipe.OutputFileName,
            OutputHash = validation.OutputHash,
            OutputLengthBytes = validation.ByteLength,
            DurationMs = validation.Probe.DurationMs,
            Width = validation.Probe.Width,
            Height = validation.Probe.Height,
            VideoCodec = validation.Probe.VideoCodec,
            AudioCodec = validation.Probe.AudioCodec,
            State = "Prepared"
        };
        manifest.WriteAtomically(recipe.ManifestFullPath);

        File.Move(recipe.TemporaryFullPath, recipe.OutputFullPath, overwrite: false);
        manifest = manifest with { State = "Committed" };
        manifest.WriteAtomically(recipe.ManifestFullPath);

        job.ReservedBytes = null;
        await PublishQualityAsync(job, recipe.OutputFullPath, recipe.ManifestFullPath, validation, plan.Profile, plan.ScaledWidth, plan.ScaledHeight, cancellationToken);
        await CompleteAttemptAsync(attempt, exitCode: 0, failure: null, recipe.TemporaryFullPath, cancellationToken);
        await TransitionAsync(job, PreparationJobState.Succeeded, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        // The new copy counts against the budget; make room without touching it
        // (it was just accessed, so LRU evicts older entries first).
        await evictionService.EvictUntilUnderLimitAsync(cacheLimit, cancellationToken);

        logger.LogInformation("Prepared {Output} ({Profile}) for lesson {LessonId}.", recipe.OutputFileName, plan.Profile, job.LessonId);
        return new PreparationOutcome(PreparationJobState.Succeeded, null, null);
    }

    private async Task<PreparationOutcome> EncodeAndPublishAsync(
        PreparationJobEntity job,
        PreparationAttemptEntity attempt,
        string videoFullPath,
        string videoRelativePath,
        string? companionFullPath,
        string? companionRelativePath,
        ProbeMetadata? probe,
        IReadOnlyList<string> recipeArguments,
        string recipeVersion,
        string? expectedAudioCodec,
        string? outputDirectoryOverride,
        Func<string, string> outputNameFactory,
        CancellationToken cancellationToken)
    {
        var fingerprint = SourceFingerprint.Compute(
        [
            ("video", videoRelativePath, videoFullPath),
            .. companionRelativePath is not null && companionFullPath is not null
                ? new[] { ("companion-audio", companionRelativePath, companionFullPath) }
                : Array.Empty<(string, string, string)>()
        ]);

        var outputDirectory = outputDirectoryOverride ?? Path.GetDirectoryName(videoFullPath)!;
        var outputFileName = outputNameFactory(fingerprint);
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

        var arguments = recipeArguments
            .Select(argument => argument == "{output}" ? temporaryFullPath : argument)
            .ToList();

        var sourceDuration = probe?.DurationMs;
        await context.SaveChangesAsync(cancellationToken);

        var result = await ffmpegAdapter.RunAsync(
            arguments,
            async seconds =>
            {
                if (sourceDuration is > 0)
                {
                    job.Progress = Math.Clamp(seconds * 1000.0 / sourceDuration.Value, 0, 1);
                }

                await Task.CompletedTask;
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
        var validation = await validator.ValidateAsync(temporaryFullPath, sourceDuration, "h264", expectedAudioCodec, cancellationToken);
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
            RecipeVersion = recipeVersion,
            Sources = [new ManifestSource("video", videoRelativePath, fingerprint)],
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

    private static string SanitizeStem(string relativePath)
    {
        var stem = Path.GetFileNameWithoutExtension(relativePath);
        var builder = new global::System.Text.StringBuilder(stem.Length);
        foreach (var character in stem)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is ' ' or '-' or '_' or '.' ? character : '-');
        }

        return builder.ToString().Trim();
    }

    private void TryDeleteTemporary(string temporaryFullPath)
    {
        try
        {
            File.Delete(temporaryFullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The partial encode {Path} could not be deleted.", temporaryFullPath);
        }
    }

    private async Task PublishQualityAsync(
        PreparationJobEntity job,
        string outputFullPath,
        string manifestFullPath,
        OutputValidation validation,
        string profile,
        int scaledWidth,
        int scaledHeight,
        CancellationToken cancellationToken)
    {
        var rendition = await context.Renditions
            .SingleOrDefaultAsync(candidate => candidate.LessonId == job.LessonId
                && candidate.SourceGeneration == job.SourceGeneration
                && candidate.Purpose == RenditionPurpose.Quality
                && candidate.Profile == profile, cancellationToken);

        if (rendition is null)
        {
            rendition = new RenditionEntity
            {
                LessonId = job.LessonId,
                SourceGeneration = job.SourceGeneration,
                Purpose = RenditionPurpose.Quality,
                RetentionClass = RenditionRetention.Quality,
                Profile = profile,
                RelativePath = RelativeToLibraryRoot(outputFullPath),
                ManifestPath = RelativeToLibraryRoot(manifestFullPath)
            };
            context.Renditions.Add(rendition);
        }

        rendition.Status = RenditionStatus.Ready;
        rendition.RecipeVersion = QualityProfiles.RecipeVersionFor(profile);
        rendition.RelativePath = RelativeToLibraryRoot(outputFullPath);
        rendition.ManifestPath = RelativeToLibraryRoot(manifestFullPath);
        rendition.Width = scaledWidth;
        rendition.Height = scaledHeight;
        rendition.VideoCodec = validation.Probe.VideoCodec;
        rendition.AudioCodec = validation.Probe.AudioCodec;
        rendition.ByteLength = validation.ByteLength;
        rendition.OutputHash = validation.OutputHash;
        rendition.LastAccessUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        rendition.Revision++;

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Registers a recovered output through the publisher that matches the job's purpose.
    /// Publishing every recovered job as a compatibility copy registered quality outputs as
    /// Permanent with no profile, which escapes the cache budget entirely and can displace
    /// the lesson's real compatibility rendition.
    /// </summary>
    private async Task<bool> TryAdoptRecoveredOutputAsync(
        PreparationJobEntity job,
        PreparationManifest manifest,
        string manifestPath,
        string outputPath,
        OutputValidation validation,
        CancellationToken cancellationToken)
    {
        if (job.Purpose == RenditionPurpose.Quality)
        {
            if (!QualityProfiles.IsKnown(job.Profile))
            {
                logger.LogWarning(
                    "Quality job {JobId} carries the unknown profile {Profile}; its output is left for inspection.",
                    job.Id, job.Profile ?? "(none)");
                return false;
            }

            (manifest with { State = "Committed" }).WriteAtomically(manifestPath);
            await PublishQualityAsync(
                job,
                outputPath,
                manifestPath,
                validation,
                job.Profile!,
                validation.Probe.Width ?? 0,
                validation.Probe.Height ?? 0,
                cancellationToken);
            return true;
        }

        (manifest with { State = "Committed" }).WriteAtomically(manifestPath);
        await PublishAsync(job, null, outputPath, manifestPath, manifest.OutputFileName, validation, cancellationToken);
        return true;
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
            try
            {
                if (context.Entry(job).State == EntityState.Detached) context.Attach(job);
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
                var manifestPath = job.ManifestRelativePath is not null && LibraryPathGuard.TryResolveWithin(appOptions.Value.LibraryRoot, job.ManifestRelativePath, out var safeManifest) ? safeManifest : null;
                var outputPath = job.OutputRelativePath is not null && LibraryPathGuard.TryResolveWithin(appOptions.Value.LibraryRoot, job.OutputRelativePath, out var safeOutput) ? safeOutput : null;
                var recordedTemporary = await context.PreparationAttempts.Where(a => a.JobId == job.Id && a.TempRelativePath != null)
                    .OrderByDescending(a => a.Id).Select(a => a.TempRelativePath).FirstOrDefaultAsync(cancellationToken);
                // Compatibility temporaries sit beside the source; quality temporaries sit in
                // the managed quality area. Both are owned by this job and identified by its id.
                var qualityDirectory = Path.Combine(
                    appOptions.Value.LibraryRoot, ".tutsvideoplayer", "quality",
                    job.LessonId.ToString(CultureInfo.InvariantCulture),
                    job.SourceGeneration.ToString(CultureInfo.InvariantCulture));
                var temporary = recordedTemporary is not null
                    && LibraryPathGuard.TryResolveWithin(appOptions.Value.LibraryRoot, recordedTemporary, out var safeTemporary)
                    && (Path.GetDirectoryName(safeTemporary) == outputDirectory
                        || Path.GetDirectoryName(safeTemporary) == Path.GetFullPath(qualityDirectory))
                    && Path.GetFileName(safeTemporary).EndsWith($".mp4.{job.Id.ToString(CultureInfo.InvariantCulture)}.part", StringComparison.Ordinal)
                        ? safeTemporary : null;

                var manifest = manifestPath is not null ? PreparationManifest.TryRead(manifestPath) : null;

                // A recorded output path whose file was never renamed into place is not an
                // unverifiable output — it is an interrupted encode, and the partial file
                // still has to be cleaned up and the job requeued.
                if (outputPath is not null && File.Exists(outputPath))
                {
                    var components = new List<(string Role, string RelativePath, string AbsolutePath)> { ("video", video.RelativePath, videoFullPath) };
                    foreach (var component in job.Lesson.SourceComponents.Where(c => c.Generation == job.SourceGeneration && c.Role == SourceComponentRole.CompanionAudio))
                    {
                        if (!LibraryPathGuard.TryResolveWithin(appOptions.Value.LibraryRoot, component.RelativePath, out var audio))
                            throw new IOException("Companion source is unavailable during recovery.");
                        components.Add(("companion-audio", component.RelativePath, audio));
                    }
                    var fingerprint = SourceFingerprint.Compute(components);
                    if (manifest is not null && manifestPath is not null
                        && job.SourceGeneration == job.Lesson.SourceGeneration
                        && manifest.LessonId == job.LessonId && manifest.SourceGeneration == job.SourceGeneration
                        && manifest.RecipeVersion == job.RecipeVersion
                        && manifest.Sources.Count > 0 && manifest.Sources[0].Fingerprint == fingerprint
                        && PreparationArtifacts.IsVerifiedOutput(outputPath, manifest))
                    {
                        var validation = await validator.ValidateAsync(outputPath, job.Lesson.DurationMs, manifest.VideoCodec, manifest.AudioCodec, cancellationToken);
                        if (validation.Success && await TryAdoptRecoveredOutputAsync(job, manifest, manifestPath, outputPath, validation, cancellationToken))
                        {
                            if (job.State is PreparationJobState.Running or PreparationJobState.Validating)
                                await TransitionAsync(job, PreparationJobState.Interrupted, cancellationToken);
                            await TransitionAsync(job, PreparationJobState.Succeeded, cancellationToken);
                            continue;
                        }
                    }
                    job.ErrorCode = "unverified-output";
                    job.UserMessage = "Existing output could not be verified. Its file and manifest have been preserved.";
                    if (job.State is not PreparationJobState.Interrupted)
                        await TransitionAsync(job, PreparationJobState.Interrupted, cancellationToken);
                    await TransitionAsync(job, PreparationJobState.Failed, cancellationToken);
                    continue;
                }

                if (temporary is not null)
                {
                    // A partial encode is never a rendition; drop it and requeue.
                    File.Delete(temporary);
                }

                await RequeueAsync(job, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Could not recover preparation {JobId}; continuing with remaining jobs.", job.Id);
                context.ChangeTracker.Clear();
            }
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
        if (target is PreparationJobState.Succeeded or PreparationJobState.Failed or PreparationJobState.Blocked or PreparationJobState.Interrupted)
        {
            job.LeaseOwner = null;
            job.LeaseExpiresUtcMs = null;
        }
        if (target == PreparationJobState.Succeeded)
        {
            job.Progress = 1;
            job.ErrorCode = null;
            job.UserMessage = null;
        }
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
        if (tempRelativePath is not null)
            attempt.TempRelativePath = Path.IsPathRooted(tempRelativePath) ? RelativeToLibraryRoot(tempRelativePath) : tempRelativePath;
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
            await RequeueAsync(job, cancellationToken);
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
        if (job.State is PreparationJobState.Running or PreparationJobState.Validating or PreparationJobState.Publishing)
        {
            await TransitionAsync(job, PreparationJobState.Interrupted, cancellationToken);
        }
        if (job.State != PreparationJobState.Queued)
        {
            await TransitionAsync(job, job.Attempt >= MaxAutomaticAttempts ? PreparationJobState.Failed : PreparationJobState.Queued, cancellationToken);
        }
        job.Progress = null;

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
        var provenanceMatches = manifest is not null
            && manifest.LessonId == job.LessonId && manifest.SourceGeneration == job.SourceGeneration
            && manifest.RecipeVersion == job.RecipeVersion
            && manifest.Sources.Count > 0 && manifest.Sources[0].Fingerprint == fingerprint
            && PreparationArtifacts.IsVerifiedOutput(outputFullPath, manifest);
        var validation = provenanceMatches
            ? await validator.ValidateAsync(outputFullPath, sourceDurationMs, manifest!.VideoCodec, manifest.AudioCodec, cancellationToken)
            : OutputValidation.Fail("The existing output does not have matching verified provenance.");
        if (validation.Success)
        {
            (manifest! with { State = "Committed" }).WriteAtomically(manifestFullPath);

            await CompleteAttemptAsync(attempt, 0, null, null, cancellationToken);
            await PublishAsync(job, attempt, outputFullPath, manifestFullPath, outputFileName, validation, cancellationToken);
            await TransitionAsync(job, PreparationJobState.Succeeded, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            return new PreparationOutcome(PreparationJobState.Succeeded, null, null);
        }

        // The existing file is not a valid current output; quarantine nothing and
        // report failure so a human can inspect it. We never overwrite it blindly.

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
        var mount = PreparationArtifacts.FindContainingMount(outputDirectory, DriveInfo.GetDrives().Select(drive => drive.Name));
        var drive = mount is null ? null : new DriveInfo(mount);
        if (drive is not null && drive.AvailableFreeSpace < preparationOptions.Value.DiskReserveBytes)
        {
            message = $"Not enough free space to prepare this version. {drive.AvailableFreeSpace / 1_000_000_000.0:0.#} GB free, {preparationOptions.Value.DiskReserveBytes / 1_000_000_000.0:0.#} GB required.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private string RelativeToLibraryRoot(string fullPath) =>
        Path.GetRelativePath(LibraryPathGuard.ResolveRoot(appOptions.Value.LibraryRoot) ?? throw new IOException("Library root is unavailable."), fullPath).Replace('\\', '/');

    private static bool IsTransient(string? error) =>
        error is not null
        && (error.Contains("I/O error", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No space left", StringComparison.OrdinalIgnoreCase)
            || error.Contains("could not be started", StringComparison.OrdinalIgnoreCase));
}