using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Core.Learning;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;
using TutsVideoPlayer.Web.Features.Learning;
using TutsVideoPlayer.Web.Models;

namespace TutsVideoPlayer.IntegrationTests;

/// <summary>
/// Revision is an EF concurrency token, so a writer can pass every in-memory check and still
/// lose at SaveChanges. These tests inject that loss deterministically: a competing update is
/// committed from another connection at the exact moment the unit of work saves.
/// </summary>
public sealed class LearningConcurrencyTests : IAsyncLifetime
{
    private string _databasePath = null!;
    private SqliteConnection _connection = null!;
    private AppDbContext _context = null!;
    private LearningService _learning = null!;
    private CompetingWriteInterceptor _interceptor = null!;
    private long _lessonId;

    public async ValueTask InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuts-race-{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_databasePath}");
        _connection.Open();

        _interceptor = new CompetingWriteInterceptor($"Data Source={_databasePath}");
        _context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_interceptor)
            .Options);
        await _context.Database.MigrateAsync();

        var library = new LibraryEntity { DisplayName = "Tutorial library", LogicalIdentity = Guid.NewGuid().ToString() };
        _context.Libraries.Add(library);
        await _context.SaveChangesAsync();

        var course = new CourseEntity
        {
            LibraryId = library.Id,
            RelativeDirectory = "RaceCourse",
            DisplayTitle = "RaceCourse",
            SearchTitle = "racecourse",
            SortKey = "RaceCourse",
            Availability = CatalogAvailability.Available
        };
        _context.Courses.Add(course);
        await _context.SaveChangesAsync();

        var lesson = new LessonEntity
        {
            CourseId = course.Id,
            PrimaryRelativePath = "RaceCourse/lesson.mp4",
            Title = "lesson",
            SearchTitle = "lesson",
            SortKey = "lesson",
            DurationMs = 120_000,
            Availability = CatalogAvailability.Available
        };
        _context.Lessons.Add(lesson);
        await _context.SaveChangesAsync();
        _lessonId = lesson.Id;

        _learning = new LearningService(_context);
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // A pooled handle may still hold the temporary file; it is disposable either way.
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstProgressCreationRecoversFromACompetingInsert(bool startingSession)
    {
        var token = TestContext.Current.CancellationToken;
        _interceptor.ArmInsert(_lessonId);
        if (startingSession)
        {
            var session = await _learning.StartSessionAsync(_lessonId, token);
            Assert.NotEmpty(session.SessionId);
        }
        else
        {
            var conflict = await Assert.ThrowsAsync<RevisionConflictException>(() =>
                _learning.SetCompletionAsync(_lessonId, CompletionChoice.Completed, 0, token));
            Assert.Equal(1, conflict.CurrentRevision);
        }

        Assert.True(_interceptor.Fired);
        Assert.Equal(1, await _context.LessonProgress.CountAsync(token));
    }

    [Fact]
    public async Task AProgressWriteThatLosesTheRaceIsRetriedRatherThanFailing()
    {
        var token = TestContext.Current.CancellationToken;
        var session = await _learning.StartSessionAsync(_lessonId, token);

        // The next SaveChanges will find the row already changed by someone else.
        _interceptor.ArmOnce();

        var outcome = await _learning.WriteProgressAsync(
            long.Parse(session.SessionId),
            new ProgressWriteModel(session.LastSequence + 1, 1, 30_000, IsPlaying: true, Ended: false),
            token);

        Assert.False(outcome.Duplicate);
        Assert.Equal(30_000, outcome.AcceptedPositionMs);
        Assert.True(_interceptor.Fired);

        var stored = await _context.LessonProgress.AsNoTracking()
            .SingleAsync(progress => progress.LessonId == _lessonId, token);
        Assert.Equal(30_000, stored.PositionMs);
    }

    [Fact]
    public async Task ACompletionWriteThatLosesTheRaceReportsAStaleRevision()
    {
        var token = TestContext.Current.CancellationToken;
        // Starting a session materializes the progress row, so the injected competing write
        // lands on the completion save itself rather than on the row's creation.
        await _learning.StartSessionAsync(_lessonId, token);
        var current = await _learning.GetCompletionAsync(_lessonId, token);

        _interceptor.ArmOnce();

        // The precondition was valid when the request arrived and is genuinely stale by the
        // time the retry re-reads. That is a 412 with the current revision, not a 500.
        var conflict = await Assert.ThrowsAsync<RevisionConflictException>(
            () => _learning.SetCompletionAsync(_lessonId, CompletionChoice.Completed, current.Revision, token));

        Assert.True(conflict.CurrentRevision > current.Revision);
        Assert.True(_interceptor.Fired);
    }

    [Fact]
    public async Task AWriteThatKeepsLosingSurfacesAsARetryableConflict()
    {
        var token = TestContext.Current.CancellationToken;
        var session = await _learning.StartSessionAsync(_lessonId, token);

        // Never stops losing: the caller must still get a documented conflict, not a crash.
        _interceptor.ArmAlways();
        try
        {
            await Assert.ThrowsAsync<ConcurrentWriteException>(() => _learning.WriteProgressAsync(
                long.Parse(session.SessionId),
                new ProgressWriteModel(session.LastSequence + 1, 1, 30_000, IsPlaying: true, Ended: false),
                token));
        }
        finally
        {
            _interceptor.Disarm();
        }
    }

    /// <summary>
    /// Commits a competing revision bump from a second connection while the unit of work under
    /// test is saving, which is exactly what two overlapping HTTP requests do to each other.
    /// </summary>
    private sealed class CompetingWriteInterceptor(string connectionString) : SaveChangesInterceptor
    {
        private int _remaining;
        private bool _always;
        private long? _insertLessonId;

        public void ArmInsert(long lessonId)
        {
            ArmOnce();
            _insertLessonId = lessonId;
        }

        public bool Fired { get; private set; }

        public void ArmOnce()
        {
            _remaining = 1;
            _always = false;
            Fired = false;
        }

        public void ArmAlways()
        {
            _always = true;
            Fired = false;
        }

        public void Disarm()
        {
            _always = false;
            _remaining = 0;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_always && _remaining <= 0)
            {
                return result;
            }

            if (!_always)
            {
                _remaining--;
            }

            Fired = true;

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            if (_insertLessonId is { } lessonId)
            {
                command.CommandText = "INSERT INTO LessonProgress (LessonId, SourceGeneration, PositionMs, MaxObservedPositionMs, AutomaticCompleted, LastSequence, LastWatchedUtcMs, Revision) VALUES ($id, 1, 0, 0, 0, 0, 0, 1)";
                command.Parameters.AddWithValue("$id", lessonId);
                _insertLessonId = null;
            }
            else
            {
                command.CommandText = "UPDATE LessonProgress SET Revision = Revision + 1";
            }
            await command.ExecuteNonQueryAsync(cancellationToken);

            return result;
        }
    }
}
