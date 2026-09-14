using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Core.Subtitles;
using TutsVideoPlayer.Infrastructure.Catalog;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;
using TutsVideoPlayer.Web.Features.Library;

namespace TutsVideoPlayer.Web.Features.Subtitles;

public sealed class SubtitleCandidateModel
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string? Language { get; init; }
    public string State { get; init; } = "Discovered";
    public string Reason { get; init; } = "course";
    public bool AutoSelectable { get; init; }
    public string? TrackUrl { get; init; }
    /// <summary>Why this candidate cannot be used, when it cannot.</summary>
    public string? Message { get; init; }
}

public sealed class SubtitleCandidatesModel
{
    public IReadOnlyList<SubtitleCandidateModel> Candidates { get; init; } = [];

    /// <summary>What automatic matching picks, independent of any manual preference.</summary>
    public string? AutoSelectedId { get; init; }

    /// <summary>The learner's stored preference, which may name a track that is now missing.</summary>
    public string? ManualId { get; init; }

    /// <summary>
    /// The track that should actually be shown: the manual preference when it is usable,
    /// otherwise the automatic match. This is deliberately independent of whether subtitles
    /// are switched on, so turning them back on restores a track instead of showing none.
    /// </summary>
    public string? ResolvedId { get; init; }

    public string? AmbiguityMessage { get; init; }
    public bool SubtitlesEnabled { get; init; }
}

public sealed record SubtitleSelectionModel(string? SubtitleTrackId, bool Automatic);

public sealed class SubtitleService(AppDbContext context, IOptions<AppOptions> options, ILogger<SubtitleService> logger)
{
    public const string NormalizationVersion = "v1";

    /// <summary>Sort/selection tier for a track whose sidecar is gone: listed last, never chosen.</summary>
    private const int MissingTier = 9;

    private static readonly string[] SubtitleDirectoryNames = ["Subtitle", "Subtitles", "Subs"];

    public async Task<SubtitleCandidatesModel> GetCandidatesAsync(long lessonId, CancellationToken cancellationToken)
    {
        var lesson = await context.Lessons.AsNoTracking()
            .Where(candidate => candidate.Id == lessonId)
            .Select(candidate => new { candidate.Id, candidate.CourseId, candidate.PrimaryRelativePath, candidate.Title })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Lesson not found.");

        var tracks = await context.SubtitleTracks.AsNoTracking()
            .Where(track => track.CourseId == lesson.CourseId)
            .OrderBy(track => track.RelativePath)
            .ToListAsync(cancellationToken);

        var manualId = await context.SubtitleAssociations.AsNoTracking()
            .Where(association => association.LessonId == lessonId && association.Origin == SubtitleAssociationOrigin.Manual)
            .Select(association => (long?)association.SubtitleTrackId)
            .SingleOrDefaultAsync(cancellationToken);

        var preferences = await context.Preferences.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var videoDirectory = DirectoryOf(lesson.PrimaryRelativePath);
        var videoStem = Path.GetFileNameWithoutExtension(lesson.PrimaryRelativePath);
        var displayTitle = DisplayTitle.FromFileName(lesson.PrimaryRelativePath);
        var normalizedTitle = DisplayTitle.NormalizeForSearch(displayTitle);

        var candidates = new List<(SubtitleCandidateModel Model, int Tier, long TrackId)>();
        var tierAmbiguity = new Dictionary<int, int>();

        foreach (var track in tracks)
        {
            SubtitleConverter.TrySplitLanguageSuffix(
                Path.GetFileNameWithoutExtension(track.RelativePath), out var baseName, out _);
            var tier = Classify(track, videoDirectory, videoStem, baseName, normalizedTitle);
            tierAmbiguity[tier] = tierAmbiguity.GetValueOrDefault(tier) + 1;

            var missing = track.Availability == CatalogAvailability.Missing;
            var usable = !missing && track.ParseStatus is "Discovered" or "Normalized";
            candidates.Add((
                new SubtitleCandidateModel
                {
                    Id = track.Id.ToString(CultureInfo.InvariantCulture),
                    Label = track.Language is not null ? $"{track.Language.ToUpperInvariant()} · {FileNameOf(track.RelativePath)}" : FileNameOf(track.RelativePath),
                    Language = track.Language,
                    State = missing ? "Missing" : track.ParseStatus,
                    Reason = ReasonFor(tier),
                    AutoSelectable = !missing && tier is >= 1 and <= 3,
                    TrackUrl = usable ? $"/media/subtitles/{track.Id.ToString(CultureInfo.InvariantCulture)}.vtt" : null,
                    Message = missing
                        ? "This subtitle file is no longer in the library."
                        : track.ParseStatus == "Failed" ? track.ParseError : null
                },
                missing ? MissingTier : tier,
                track.Id));
        }

        var ordered = candidates
            .OrderBy(entry => entry.Tier)
            .ThenBy(entry => entry.Model.Label, StringComparer.Ordinal)
            .Select(entry => entry.Model)
            .ToList();

        // Automatic matching is resolved on its own terms, so returning to Automatic can be
        // answered with a real track rather than "nothing selected".
        string? autoSelected = null;
        string? ambiguityMessage = null;
        foreach (var tier in new[] { 1, 2, 3 })
        {
            var tierCandidates = candidates.Where(entry => entry.Tier == tier).ToList();
            if (tierCandidates.Count == 1)
            {
                autoSelected = tierCandidates[0].TrackId.ToString(CultureInfo.InvariantCulture);
                break;
            }

            if (tierCandidates.Count > 1)
            {
                ambiguityMessage = "More than one subtitle file matches this lesson. Choose subtitles to pick one.";
                break;
            }
        }

        var manualCandidate = manualId.HasValue
            ? candidates.FirstOrDefault(entry => entry.TrackId == manualId.Value)
            : default;
        var manualUsable = manualId.HasValue && manualCandidate.Model is not null && manualCandidate.Tier != MissingTier;

        if (manualId.HasValue && !manualUsable)
        {
            // The preference is remembered rather than forgotten, and the reason it cannot be
            // honoured is stated instead of another file being substituted silently.
            ambiguityMessage = "The preferred subtitle file is no longer in the library; the preference is kept. Refresh library, or choose another subtitle file.";
        }
        else if (manualUsable)
        {
            // An explicit choice settles any ambiguity between equal automatic candidates.
            ambiguityMessage = null;
        }

        var resolvedId = manualUsable
            ? manualId!.Value.ToString(CultureInfo.InvariantCulture)
            : manualId.HasValue ? null : autoSelected;

        return new SubtitleCandidatesModel
        {
            Candidates = ordered,
            AutoSelectedId = autoSelected,
            ManualId = manualId?.ToString(CultureInfo.InvariantCulture),
            ResolvedId = resolvedId,
            AmbiguityMessage = ambiguityMessage,
            SubtitlesEnabled = preferences?.SubtitleEnabled ?? true
        };
    }

    public async Task<SubtitleSelectionModel> SelectAsync(long lessonId, long? trackId, CancellationToken cancellationToken)
    {
        var lesson = await context.Lessons.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == lessonId, cancellationToken)
            ?? throw new InvalidOperationException("Lesson not found.");

        var existing = await context.SubtitleAssociations
            .SingleOrDefaultAsync(association => association.LessonId == lessonId && association.Origin == SubtitleAssociationOrigin.Manual, cancellationToken);

        if (trackId is null)
        {
            if (existing is not null)
            {
                context.SubtitleAssociations.Remove(existing);
                await context.SaveChangesAsync(cancellationToken);
            }

            return new SubtitleSelectionModel(null, Automatic: true);
        }

        var track = await context.SubtitleTracks.AsNoTracking()
            .SingleOrDefaultAsync(track => track.Id == trackId.Value, cancellationToken);
        if (track is null)
        {
            throw new InvalidOperationException("Subtitle track not found.");
        }

        if (track.CourseId != lesson.CourseId)
        {
            throw new InvalidOperationException("The selected subtitle track belongs to a different course.");
        }

        if (existing is not null && existing.SubtitleTrackId == trackId.Value)
        {
            return new SubtitleSelectionModel(trackId.Value.ToString(CultureInfo.InvariantCulture), Automatic: false);
        }

        // SubtitleTrackId is part of the identifying key, so switching tracks is a delete
        // followed by an insert rather than a key mutation. Both halves commit together:
        // a failed insert must not leave the lesson with no preference at all.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (existing is not null)
            {
                context.SubtitleAssociations.Remove(existing);
                await context.SaveChangesAsync(cancellationToken);
            }

            context.SubtitleAssociations.Add(new SubtitleAssociationEntity
            {
                LessonId = lessonId,
                SubtitleTrackId = trackId.Value,
                Origin = SubtitleAssociationOrigin.Manual,
                Priority = 0,
                SourceGeneration = null
            });
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            context.ChangeTracker.Clear();
            throw;
        }

        return new SubtitleSelectionModel(trackId.Value.ToString(CultureInfo.InvariantCulture), Automatic: false);
    }

    public async Task<SubtitleDelivery> GetDeliveryAsync(long trackId, CancellationToken cancellationToken)
    {
        var track = await context.SubtitleTracks.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == trackId, cancellationToken)
            ?? throw new InvalidOperationException("Subtitle track not found.");

        // The identity of the source as it is on disk right now. Scanning rewrites the row's
        // size and modification time without clearing the normalized artifact, and a file can
        // also be edited between scans, so the live file is what the cache is judged against.
        var sourceIdentity = ReadSourceIdentity(track);

        if (track.ParseStatus == "Failed" && SourceMatchesNormalization(track, sourceIdentity))
        {
            // Only refuse again while the file is the same one that failed; a corrected file
            // is given another chance instead of staying broken until the next restart.
            throw new SubtitleFormatException(track.ParseError ?? "The subtitle file could not be converted.");
        }

        var normalizedPath = track.NormalizedRelativePath;
        var normalizedFullPath = string.IsNullOrEmpty(normalizedPath)
            ? null
            : Path.IsPathRooted(normalizedPath)
                ? normalizedPath
                : Path.Combine(ResolveAppDataDirectory(), normalizedPath);

        var cacheUsable = normalizedFullPath is not null
            && track.NormalizationVersion == NormalizationVersion
            && track.ParseStatus == "Normalized"
            && SourceMatchesNormalization(track, sourceIdentity)
            && File.Exists(normalizedFullPath);

        if (!cacheUsable)
        {
            var result = await NormalizeAsync(track, cancellationToken);
            if (!result.Success)
            {
                throw new SubtitleFormatException(result.Error ?? "The subtitle file could not be converted.");
            }

            normalizedFullPath = result.NormalizedFullPath;
        }

        return new SubtitleDelivery(normalizedFullPath!, track.Fingerprint ?? track.Id.ToString(CultureInfo.InvariantCulture));
    }

    public sealed record SubtitleDelivery(string FullPath, string Fingerprint);

    public sealed record NormalizationOutcome(bool Success, string? NormalizedFullPath, string? Error);

    public async Task<NormalizationOutcome> NormalizeAsync(SubtitleTrackEntity track, CancellationToken cancellationToken)
    {
        var trackEntity = await context.SubtitleTracks
            .SingleOrDefaultAsync(candidate => candidate.Id == track.Id, cancellationToken)
            ?? throw new InvalidOperationException("Subtitle track not found.");

        if (!LibraryPathGuard.TryResolveWithin(options.Value.LibraryRoot, trackEntity.RelativePath, out var sourceFullPath))
        {
            await MarkFailedAsync(trackEntity, "The subtitle file is not available inside the library root.", cancellationToken);
            return new NormalizationOutcome(false, null, "The subtitle file is not available inside the library root.");
        }

        var file = new FileInfo(sourceFullPath);
        if (file.Length > SubtitleLimits.MaxSourceBytes)
        {
            await MarkFailedAsync(trackEntity, $"The subtitle file exceeds the {SubtitleLimits.MaxSourceBytes} byte limit.", cancellationToken);
            return new NormalizationOutcome(false, null, "The subtitle file is too large to convert.");
        }

        var sourceLength = file.Length;
        var sourceModifiedUtcMs = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds();

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(sourceFullPath, cancellationToken);
        }
        catch (IOException)
        {
            const string message = "The subtitle file could not be read.";
            await MarkFailedAsync(trackEntity, message, sourceLength, sourceModifiedUtcMs, cancellationToken);
            return new NormalizationOutcome(false, null, message);
        }

        if (!TryDecodeStrict(bytes, out var text, out var decodeError))
        {
            await MarkFailedAsync(trackEntity, decodeError, sourceLength, sourceModifiedUtcMs, cancellationToken);
            return new NormalizationOutcome(false, null, decodeError);
        }

        var isWebVtt = trackEntity.Format == "Vtt";
        var conversion = SubtitleConverter.ConvertToWebVtt(text, isAlreadyWebVtt: isWebVtt);
        if (!conversion.Success)
        {
            await MarkFailedAsync(
                trackEntity,
                conversion.Error ?? "The subtitle file could not be converted.",
                sourceLength,
                sourceModifiedUtcMs,
                cancellationToken);
            return new NormalizationOutcome(false, null, conversion.Error);
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 32).ToLowerInvariant();
        var appData = ResolveAppDataDirectory();
        var subtitleDirectory = Path.Combine(appData, "subtitles");
        Directory.CreateDirectory(subtitleDirectory);
        var normalizedRelative = Path.Combine("subtitles", $"{trackEntity.Id.ToString(CultureInfo.InvariantCulture)}-{fingerprint[..8]}.vtt");
        var normalizedFullPath = Path.Combine(appData, normalizedRelative);
        await File.WriteAllTextAsync(normalizedFullPath, conversion.WebVtt!, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        trackEntity.ParseStatus = "Normalized";
        trackEntity.ParseError = null;
        trackEntity.Fingerprint = fingerprint;
        trackEntity.NormalizedRelativePath = normalizedRelative;
        trackEntity.NormalizationVersion = NormalizationVersion;
        trackEntity.NormalizedSourceLengthBytes = sourceLength;
        trackEntity.NormalizedSourceModifiedUtcMs = sourceModifiedUtcMs;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Normalized subtitle track {TrackId} with {CueCount} cues.", trackEntity.Id, conversion.CueCount);
        return new NormalizationOutcome(true, normalizedFullPath, null);
    }

    private async Task MarkFailedAsync(
        SubtitleTrackEntity trackEntity,
        string message,
        long? sourceLengthBytes,
        long? sourceModifiedUtcMs,
        CancellationToken cancellationToken)
    {
        trackEntity.ParseStatus = "Failed";
        trackEntity.ParseError = message;
        trackEntity.NormalizedRelativePath = null;
        trackEntity.Fingerprint = null;
        // The failure is recorded against the exact bytes that failed, so replacing the file
        // clears the refusal on the next request.
        trackEntity.NormalizedSourceLengthBytes = sourceLengthBytes;
        trackEntity.NormalizedSourceModifiedUtcMs = sourceModifiedUtcMs;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkFailedAsync(SubtitleTrackEntity trackEntity, string message, CancellationToken cancellationToken) =>
        await MarkFailedAsync(trackEntity, message, null, null, cancellationToken);

    private sealed record SourceIdentity(bool Exists, long LengthBytes, long ModifiedUtcMs);

    private SourceIdentity ReadSourceIdentity(SubtitleTrackEntity track)
    {
        if (!LibraryPathGuard.TryResolveWithin(options.Value.LibraryRoot, track.RelativePath, out var fullPath))
        {
            return new SourceIdentity(false, 0, 0);
        }

        try
        {
            var file = new FileInfo(fullPath);
            return file.Exists
                ? new SourceIdentity(true, file.Length, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds())
                : new SourceIdentity(false, 0, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SourceIdentity(false, 0, 0);
        }
    }

    private static bool SourceMatchesNormalization(SubtitleTrackEntity track, SourceIdentity source) =>
        source.Exists
        && track.NormalizedSourceLengthBytes == source.LengthBytes
        && track.NormalizedSourceModifiedUtcMs == source.ModifiedUtcMs;

    /// <summary>
    /// Decodes subtitle bytes without a replacement fallback. <c>Encoding.UTF8</c> substitutes
    /// U+FFFD for invalid bytes, which silently corrupts captions instead of reporting a file
    /// that cannot be read; SUB-01 requires the file to be reported. A byte-order mark selects
    /// UTF-16 or UTF-32 explicitly; anything else must be valid UTF-8.
    /// </summary>
    private static bool TryDecodeStrict(byte[] bytes, out string text, out string error)
    {
        text = string.Empty;
        error = string.Empty;

        var (encoding, preambleLength) = DetectEncoding(bytes);
        try
        {
            text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            return true;
        }
        catch (DecoderFallbackException)
        {
            error = encoding is UTF8Encoding
                ? "The subtitle file is not valid UTF-8 text."
                : "The subtitle file could not be decoded with the encoding its byte-order mark declares.";
            return false;
        }
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), 3);
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true), 4);
        }

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true), 4);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true), 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true), 2);
        }

        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), 0);
    }

    private string ResolveAppDataDirectory() =>
        Path.IsPathRooted(options.Value.DataDirectory)
            ? options.Value.DataDirectory
            : Path.Combine(AppContext.BaseDirectory, options.Value.DataDirectory);

    private static int Classify(
        SubtitleTrackEntity track,
        string videoDirectory,
        string videoStem,
        string trackBaseStem,
        string normalizedTitle)
    {
        var trackStem = Path.GetFileNameWithoutExtension(track.RelativePath);
        var trackDirectory = DirectoryOf(track.RelativePath);

        if (string.Equals(trackStem, videoStem, StringComparison.Ordinal)
            && string.Equals(trackDirectory, videoDirectory, StringComparison.Ordinal))
        {
            return 1;
        }

        var segments = track.RelativePath.Split('/');
        var inSubtitleDirectory = segments.Any(segment =>
            SubtitleDirectoryNames.Any(name => string.Equals(segment, name, StringComparison.OrdinalIgnoreCase)));
        if (inSubtitleDirectory && string.Equals(trackBaseStem, videoStem, StringComparison.Ordinal))
        {
            return 2;
        }

        var strippedTrackStem = DisplayTitle.FromFolderName(trackBaseStem);
        var normalizedTrackStem = DisplayTitle.NormalizeForSearch(strippedTrackStem);
        if (normalizedTrackStem.Length > 0
            && (normalizedTrackStem == normalizedTitle
                || normalizedTrackStem.StartsWith(normalizedTitle + " ", StringComparison.Ordinal)))
        {
            return 3;
        }

        return 4;
    }

    private static string ReasonFor(int tier) => tier switch
    {
        0 => "manual",
        1 => "adjacent file",
        2 => "subtitles folder",
        3 => "title match",
        _ => "course file"
    };

    private static string DirectoryOf(string relativePath)
    {
        var segments = relativePath.Split('/');
        return segments.Length == 1 ? string.Empty : string.Join('/', segments[..^1]);
    }

    private static string FileNameOf(string relativePath) => relativePath.Split('/')[^1];
}

public sealed class SubtitleFormatException(string message) : InvalidOperationException(message);