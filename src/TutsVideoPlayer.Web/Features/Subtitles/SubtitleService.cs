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
}

public sealed class SubtitleCandidatesModel
{
    public IReadOnlyList<SubtitleCandidateModel> Candidates { get; init; } = [];
    public string? AutoSelectedId { get; init; }
    public string? ManualId { get; init; }
    public string? AmbiguityMessage { get; init; }
    public bool SubtitlesEnabled { get; init; }
}

public sealed record SubtitleSelectionModel(string? SubtitleTrackId, bool Automatic);

public sealed class SubtitleService(AppDbContext context, IOptions<AppOptions> options, ILogger<SubtitleService> logger)
{
    public const string NormalizationVersion = "v1";
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

            var usable = track.ParseStatus is "Discovered" or "Normalized";
            candidates.Add((
                new SubtitleCandidateModel
                {
                    Id = track.Id.ToString(CultureInfo.InvariantCulture),
                    Label = track.Language is not null ? $"{track.Language.ToUpperInvariant()} · {FileNameOf(track.RelativePath)}" : FileNameOf(track.RelativePath),
                    Language = track.Language,
                    State = track.ParseStatus,
                    Reason = ReasonFor(tier),
                    AutoSelectable = tier is >= 1 and <= 3,
                    TrackUrl = usable ? $"/media/subtitles/{track.Id.ToString(CultureInfo.InvariantCulture)}.vtt" : null
                },
                tier,
                track.Id));
        }

        var ordered = candidates
            .OrderBy(entry => entry.Tier)
            .ThenBy(entry => entry.Model.Label, StringComparer.Ordinal)
            .Select(entry => entry.Model)
            .ToList();

        string? autoSelected = null;
        string? ambiguityMessage = null;
        if (manualId.HasValue)
        {
            autoSelected = manualId.Value.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
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
        }

        if (manualId.HasValue && !candidates.Any(entry => entry.TrackId == manualId.Value))
        {
            ambiguityMessage = "The preferred subtitle track is missing from this course; the library may have changed. Refresh library, or choose another subtitle file.";
        }

        return new SubtitleCandidatesModel
        {
            Candidates = ordered,
            AutoSelectedId = autoSelected,
            ManualId = manualId?.ToString(CultureInfo.InvariantCulture),
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

        if (existing is null)
        {
            context.SubtitleAssociations.Add(new SubtitleAssociationEntity
            {
                LessonId = lessonId,
                SubtitleTrackId = trackId.Value,
                Origin = SubtitleAssociationOrigin.Manual,
                Priority = 0,
                SourceGeneration = null
            });
        }
        else
        {
            existing.SubtitleTrackId = trackId.Value;
        }

        await context.SaveChangesAsync(cancellationToken);
        return new SubtitleSelectionModel(trackId.Value.ToString(CultureInfo.InvariantCulture), Automatic: false);
    }

    public async Task<SubtitleDelivery> GetDeliveryAsync(long trackId, CancellationToken cancellationToken)
    {
        var track = await context.SubtitleTracks.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == trackId, cancellationToken)
            ?? throw new InvalidOperationException("Subtitle track not found.");

        if (track.ParseStatus == "Failed")
        {
            throw new SubtitleFormatException(track.ParseError ?? "The subtitle file could not be converted.");
        }

        var normalizedPath = track.NormalizedRelativePath;
        var normalizedFullPath = string.IsNullOrEmpty(normalizedPath)
            ? null
            : Path.IsPathRooted(normalizedPath)
                ? normalizedPath
                : Path.Combine(ResolveAppDataDirectory(), normalizedPath);

        var sourceUpToDate = normalizedFullPath is not null
            && track.NormalizationVersion == NormalizationVersion
            && File.Exists(normalizedFullPath);

        if (!sourceUpToDate)
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

        string text;
        try
        {
            using var stream = file.OpenRead();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or DecoderFallbackException)
        {
            var message = exception is DecoderFallbackException
                ? "The subtitle file is not valid UTF-8 text."
                : "The subtitle file could not be read.";
            await MarkFailedAsync(trackEntity, message, cancellationToken);
            return new NormalizationOutcome(false, null, message);
        }

        var isWebVtt = trackEntity.Format == "Vtt";
        var conversion = SubtitleConverter.ConvertToWebVtt(text, isAlreadyWebVtt: isWebVtt);
        if (!conversion.Success)
        {
            await MarkFailedAsync(trackEntity, conversion.Error ?? "The subtitle file could not be converted.", cancellationToken);
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
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Normalized subtitle track {TrackId} with {CueCount} cues.", trackEntity.Id, conversion.CueCount);
        return new NormalizationOutcome(true, normalizedFullPath, null);
    }

    private async Task MarkFailedAsync(SubtitleTrackEntity trackEntity, string message, CancellationToken cancellationToken)
    {
        trackEntity.ParseStatus = "Failed";
        trackEntity.ParseError = message;
        trackEntity.NormalizedRelativePath = null;
        trackEntity.Fingerprint = null;
        await context.SaveChangesAsync(cancellationToken);
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

        var normalizedTrackStem = DisplayTitle.NormalizeForSearch(trackBaseStem);
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