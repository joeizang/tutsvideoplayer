using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TutsVideoPlayer.Core.Catalog;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;

namespace TutsVideoPlayer.Infrastructure.Persistence.Configurations;

internal sealed class LibraryConfiguration : IEntityTypeConfiguration<LibraryEntity>
{
    public void Configure(EntityTypeBuilder<LibraryEntity> builder)
    {
        builder.ToTable("Libraries");
        builder.HasKey(library => library.Id);
        builder.Property(library => library.Id).ValueGeneratedOnAdd();
        builder.Property(library => library.DisplayName).IsRequired();
        builder.Property(library => library.LogicalIdentity).IsRequired();
        builder.HasIndex(library => library.LogicalIdentity).IsUnique();
        builder.Property(library => library.Revision).IsConcurrencyToken();
    }
}

internal sealed class CourseConfiguration : IEntityTypeConfiguration<CourseEntity>
{
    public void Configure(EntityTypeBuilder<CourseEntity> builder)
    {
        builder.ToTable("Courses");
        builder.HasKey(course => course.Id);
        builder.Property(course => course.Id).ValueGeneratedOnAdd();
        builder.Property(course => course.RelativeDirectory).IsRequired();
        builder.Property(course => course.DisplayTitle).IsRequired();
        builder.Property(course => course.SearchTitle).IsRequired();
        builder.Property(course => course.SortKey).IsRequired();
        builder.Property(course => course.Revision).IsConcurrencyToken();
        builder.HasOne<LibraryEntity>()
            .WithMany()
            .HasForeignKey(course => course.LibraryId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(course => new { course.LibraryId, course.RelativeDirectory }).IsUnique();
        builder.HasIndex(course => new { course.LibraryId, course.SortKey });
        builder.HasIndex(course => course.SearchTitle);
    }
}

internal sealed class LessonFolderConfiguration : IEntityTypeConfiguration<LessonFolderEntity>
{
    public void Configure(EntityTypeBuilder<LessonFolderEntity> builder)
    {
        builder.ToTable("LessonFolders");
        builder.HasKey(folder => folder.Id);
        builder.Property(folder => folder.Id).ValueGeneratedOnAdd();
        builder.Property(folder => folder.RelativeDirectory).IsRequired();
        builder.Property(folder => folder.Title).IsRequired();
        builder.Property(folder => folder.SortKey).IsRequired();
        builder.HasOne(folder => folder.ParentFolder)
            .WithMany()
            .HasForeignKey(folder => folder.ParentFolderId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(folder => folder.Course)
            .WithMany()
            .HasForeignKey(folder => folder.CourseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(folder => new { folder.CourseId, folder.RelativeDirectory }).IsUnique();
        builder.HasIndex(folder => new { folder.CourseId, folder.SortKey });
    }
}

internal sealed class LessonConfiguration : IEntityTypeConfiguration<LessonEntity>
{
    public void Configure(EntityTypeBuilder<LessonEntity> builder)
    {
        builder.ToTable("Lessons");
        builder.HasKey(lesson => lesson.Id);
        builder.Property(lesson => lesson.Id).ValueGeneratedOnAdd();
        builder.Property(lesson => lesson.SourceGeneration).HasDefaultValue(1);
        builder.Property(lesson => lesson.PrimaryRelativePath).IsRequired();
        builder.Property(lesson => lesson.Title).IsRequired();
        builder.Property(lesson => lesson.SearchTitle).IsRequired();
        builder.Property(lesson => lesson.SortKey).IsRequired();
        builder.Property(lesson => lesson.Revision).IsConcurrencyToken();
        builder.HasOne(lesson => lesson.Course)
            .WithMany(course => course.Lessons)
            .HasForeignKey(lesson => lesson.CourseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(lesson => lesson.Folder)
            .WithMany()
            .HasForeignKey(lesson => lesson.FolderId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(lesson => new { lesson.CourseId, lesson.PrimaryRelativePath }).IsUnique();
        builder.HasIndex(lesson => new { lesson.CourseId, lesson.SortKey });
        builder.HasIndex(lesson => lesson.LastSeenScanId);
        builder.ToTable(t => t.HasCheckConstraint("CK_Lessons_SourceGeneration", "SourceGeneration >= 1"));
        builder.ToTable(t => t.HasCheckConstraint("CK_Lessons_DurationMs", "DurationMs IS NULL OR DurationMs > 0"));
    }
}

internal sealed class SourceComponentConfiguration : IEntityTypeConfiguration<SourceComponentEntity>
{
    public void Configure(EntityTypeBuilder<SourceComponentEntity> builder)
    {
        builder.ToTable("SourceComponents");
        builder.HasKey(component => component.Id);
        builder.Property(component => component.Id).ValueGeneratedOnAdd();
        builder.Property(component => component.RelativePath).IsRequired();
        builder.Property(component => component.LengthBytes);
        builder.ToTable(t => t.HasCheckConstraint("CK_SourceComponents_LengthBytes", "LengthBytes >= 0"));
        builder.HasOne(component => component.Lesson)
            .WithMany(lesson => lesson.SourceComponents)
            .HasForeignKey(component => component.LessonId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(component => new { component.LessonId, component.Generation, component.Role, component.RelativePath }).IsUnique();
        builder.HasIndex(component => component.RelativePath);
    }
}

internal sealed class SubtitleTrackConfiguration : IEntityTypeConfiguration<SubtitleTrackEntity>
{
    public void Configure(EntityTypeBuilder<SubtitleTrackEntity> builder)
    {
        builder.ToTable("SubtitleTracks");
        builder.HasKey(track => track.Id);
        builder.Property(track => track.Id).ValueGeneratedOnAdd();
        builder.Property(track => track.RelativePath).IsRequired();
        builder.Property(track => track.Format).IsRequired();
        builder.Property(track => track.ParseStatus).IsRequired();
        builder.HasOne(track => track.Course)
            .WithMany()
            .HasForeignKey(track => track.CourseId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(track => track.RelativePath).IsUnique();
    }
}

internal sealed class ScanRunConfiguration : IEntityTypeConfiguration<ScanRunEntity>
{
    public void Configure(EntityTypeBuilder<ScanRunEntity> builder)
    {
        builder.ToTable("ScanRuns");
        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).ValueGeneratedOnAdd();
        builder.Property(run => run.State).IsRequired();
        builder.HasIndex(run => run.State);
    }
}

internal sealed class ScanIssueConfiguration : IEntityTypeConfiguration<ScanIssueEntity>
{
    public void Configure(EntityTypeBuilder<ScanIssueEntity> builder)
    {
        builder.ToTable("ScanIssues");
        builder.HasKey(issue => issue.Id);
        builder.Property(issue => issue.Id).ValueGeneratedOnAdd();
        builder.Property(issue => issue.Code).IsRequired();
        builder.Property(issue => issue.Message).IsRequired();
        builder.HasOne(issue => issue.ScanRun)
            .WithMany()
            .HasForeignKey(issue => issue.ScanRunId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(issue => issue.ScanRunId);
    }
}
