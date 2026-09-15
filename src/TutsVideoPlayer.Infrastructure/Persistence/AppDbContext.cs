using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Infrastructure.Persistence.Configurations;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;

namespace TutsVideoPlayer.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<LibraryEntity> Libraries => Set<LibraryEntity>();
    public DbSet<CourseEntity> Courses => Set<CourseEntity>();
    public DbSet<LessonFolderEntity> LessonFolders => Set<LessonFolderEntity>();
    public DbSet<LessonEntity> Lessons => Set<LessonEntity>();
    public DbSet<SourceComponentEntity> SourceComponents => Set<SourceComponentEntity>();
    public DbSet<SubtitleTrackEntity> SubtitleTracks => Set<SubtitleTrackEntity>();
    public DbSet<SubtitleAssociationEntity> SubtitleAssociations => Set<SubtitleAssociationEntity>();
    public DbSet<ScanRunEntity> ScanRuns => Set<ScanRunEntity>();
    public DbSet<ScanIssueEntity> ScanIssues => Set<ScanIssueEntity>();
    public DbSet<LessonProgressEntity> LessonProgress => Set<LessonProgressEntity>();
    public DbSet<PlaybackSessionEntity> PlaybackSessions => Set<PlaybackSessionEntity>();
    public DbSet<RenditionEntity> Renditions => Set<RenditionEntity>();
    public DbSet<PreferenceEntity> Preferences => Set<PreferenceEntity>();
    public DbSet<PreparationJobEntity> PreparationJobs => Set<PreparationJobEntity>();
    public DbSet<PreparationAttemptEntity> PreparationAttempts => Set<PreparationAttemptEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
