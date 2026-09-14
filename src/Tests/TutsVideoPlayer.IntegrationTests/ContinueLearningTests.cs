using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Core.Learning;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;
using TutsVideoPlayer.Web.Features.Learning;

namespace TutsVideoPlayer.IntegrationTests;

/// <summary>
/// Continue learning recommendations, against a private database so the seeded catalog states
/// (a replaced source, a lesson whose file has gone missing) stay isolated.
/// </summary>
public sealed class ContinueLearningTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private AppDbContext _context = null!;
    private LearningService _learning = null!;
    private long _libraryId;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _context.Database.MigrateAsync();

        var library = new LibraryEntity { DisplayName = "Tutorial library", LogicalIdentity = Guid.NewGuid().ToString() };
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();
        _libraryId = library.Id;

        _learning = new LearningService(_context);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<CourseEntity> AddCourseAsync(string title)
    {
        var course = new CourseEntity
        {
            LibraryId = _libraryId,
            RelativeDirectory = title,
            DisplayTitle = title,
            SearchTitle = title.ToLowerInvariant(),
            SortKey = title,
            Availability = CatalogAvailability.Available
        };
        _context.Courses.Add(course);
        await _context.SaveChangesAsync();
        return course;
    }

    private async Task<LessonEntity> AddLessonAsync(
        CourseEntity course,
        string name,
        int sourceGeneration = 1,
        CatalogAvailability availability = CatalogAvailability.Available)
    {
        var lesson = new LessonEntity
        {
            CourseId = course.Id,
            PrimaryRelativePath = $"{course.RelativeDirectory}/{name}",
            Title = name,
            SearchTitle = name.ToLowerInvariant(),
            SortKey = name,
            SourceGeneration = sourceGeneration,
            DurationMs = 60_000,
            Availability = availability
        };
        _context.Lessons.Add(lesson);
        await _context.SaveChangesAsync();
        return lesson;
    }

    private async Task AddProgressAsync(
        LessonEntity lesson,
        int sourceGeneration,
        long positionMs,
        long lastWatchedUtcMs,
        bool automaticCompleted = false,
        CompletionChoice? manual = null)
    {
        _context.LessonProgress.Add(new LessonProgressEntity
        {
            LessonId = lesson.Id,
            SourceGeneration = sourceGeneration,
            PositionMs = positionMs,
            MaxObservedPositionMs = positionMs,
            LastWatchedUtcMs = lastWatchedUtcMs,
            AutomaticCompleted = automaticCompleted,
            ManualCompletion = manual
        });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task HistoricalGenerationsDoNotSteerTheRecommendation()
    {
        var token = TestContext.Current.CancellationToken;
        var course = await AddCourseAsync("ReplacedCourse");
        var lesson = await AddLessonAsync(course, "lesson-one.mp4", sourceGeneration: 2);

        // What the reader watched before the source was replaced: near the end, and completed.
        await AddProgressAsync(lesson, sourceGeneration: 1, positionMs: 55_000, lastWatchedUtcMs: 2_000,
            automaticCompleted: true, manual: CompletionChoice.Completed);
        // What exists now: the same path, new bytes, barely opened.
        await AddProgressAsync(lesson, sourceGeneration: 2, positionMs: 0, lastWatchedUtcMs: 1_000);

        var entries = await _learning.ContinueLearningAsync(5, token);

        var entry = Assert.Single(entries);
        Assert.Equal(0, entry.PositionMs);
        Assert.Equal("resume", entry.Recommendation);
        Assert.Equal(lesson.Id.ToString(), entry.RecommendedLessonId);
        Assert.Equal(0, entry.CompletedLessons);
        Assert.True(entry.SourceChanged);
    }

    [Fact]
    public async Task AMissingRememberedLessonRecommendsAnAvailableOne()
    {
        var token = TestContext.Current.CancellationToken;
        var course = await AddCourseAsync("PartlyMissingCourse");
        var gone = await AddLessonAsync(course, "01 gone.mp4", availability: CatalogAvailability.Missing);
        var present = await AddLessonAsync(course, "02 present.mp4");

        await AddProgressAsync(gone, sourceGeneration: 1, positionMs: 12_000, lastWatchedUtcMs: 5_000);

        var entries = await _learning.ContinueLearningAsync(5, token);

        var entry = Assert.Single(entries);
        // The history is preserved and still described...
        Assert.Equal(gone.Id.ToString(), entry.LessonId);
        Assert.Equal(12_000, entry.PositionMs);
        Assert.False(entry.LessonAvailable);
        // ...but Resume must not point at a page that cannot play.
        Assert.Equal("next", entry.Recommendation);
        Assert.Equal(present.Id.ToString(), entry.RecommendedLessonId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACourseWithNothingPlayableIsReportedRatherThanLinked(bool completed)
    {
        var token = TestContext.Current.CancellationToken;
        var course = await AddCourseAsync("FullyMissingCourse");
        var gone = await AddLessonAsync(course, "only.mp4", availability: CatalogAvailability.Missing);
        await AddProgressAsync(gone, sourceGeneration: 1, positionMs: 3_000, lastWatchedUtcMs: 7_000, automaticCompleted: completed);

        var entries = await _learning.ContinueLearningAsync(5, token);

        var entry = Assert.Single(entries);
        Assert.Equal("unavailable", entry.Recommendation);
        Assert.False(entry.LessonAvailable);
    }

    [Fact]
    public async Task AnAvailableIncompleteLessonStillResumes()
    {
        var token = TestContext.Current.CancellationToken;
        var course = await AddCourseAsync("OrdinaryCourse");
        var lesson = await AddLessonAsync(course, "01 intro.mp4");
        await AddLessonAsync(course, "02 next.mp4");
        await AddProgressAsync(lesson, sourceGeneration: 1, positionMs: 20_000, lastWatchedUtcMs: 9_000);

        var entries = await _learning.ContinueLearningAsync(5, token);

        var entry = Assert.Single(entries);
        Assert.Equal("resume", entry.Recommendation);
        Assert.Equal(lesson.Id.ToString(), entry.RecommendedLessonId);
        Assert.True(entry.LessonAvailable);
        Assert.False(entry.SourceChanged);
    }
}
