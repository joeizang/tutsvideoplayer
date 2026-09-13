using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TutsVideoPlayer.Core.Learning;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;

namespace TutsVideoPlayer.Infrastructure.Persistence.Configurations;

internal sealed class LessonProgressConfiguration : IEntityTypeConfiguration<LessonProgressEntity>
{
    public void Configure(EntityTypeBuilder<LessonProgressEntity> builder)
    {
        builder.ToTable("LessonProgress");
        builder.HasKey(progress => new { progress.LessonId, progress.SourceGeneration });
        builder.Property(progress => progress.PositionMs);
        builder.Property(progress => progress.Revision).IsConcurrencyToken();
        builder.Property(progress => progress.ManualCompletion)
            .HasConversion(
                choice => (int)choice,
                value => (CompletionChoice)value);
        builder.HasOne(progress => progress.Lesson)
            .WithMany()
            .HasForeignKey(progress => progress.LessonId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(progress => progress.LastWatchedUtcMs);
        builder.ToTable(t => t.HasCheckConstraint("CK_LessonProgress_PositionMs", "PositionMs >= 0"));
        builder.ToTable(t => t.HasCheckConstraint("CK_LessonProgress_LastSequence", "LastSequence >= 0"));
    }
}

internal sealed class PlaybackSessionConfiguration : IEntityTypeConfiguration<PlaybackSessionEntity>
{
    public void Configure(EntityTypeBuilder<PlaybackSessionEntity> builder)
    {
        builder.ToTable("PlaybackSessions");
        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).ValueGeneratedOnAdd();
        builder.HasOne(session => session.Lesson)
            .WithMany()
            .HasForeignKey(session => session.LessonId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(session => new { session.LessonId, session.LastHeartbeatUtcMs });
        builder.HasIndex(session => session.LastHeartbeatUtcMs);
    }
}

internal sealed class RenditionConfiguration : IEntityTypeConfiguration<RenditionEntity>
{
    public void Configure(EntityTypeBuilder<RenditionEntity> builder)
    {
        builder.ToTable("Renditions");
        builder.HasKey(rendition => rendition.Id);
        builder.Property(rendition => rendition.Id).ValueGeneratedOnAdd();
        builder.Property(rendition => rendition.RelativePath).IsRequired();
        builder.Property(rendition => rendition.Revision).IsConcurrencyToken();
        builder.HasOne(rendition => rendition.Lesson)
            .WithMany()
            .HasForeignKey(rendition => rendition.LessonId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(rendition => new { rendition.LessonId, rendition.SourceGeneration, rendition.Purpose, rendition.Profile, rendition.RecipeVersion })
            .IsUnique();
        builder.HasIndex(rendition => rendition.RelativePath);
        builder.HasIndex(rendition => new { rendition.Status, rendition.RetentionClass });
        builder.ToTable(t => t.HasCheckConstraint("CK_Renditions_ByteLength", "ByteLength >= 0"));
    }
}

internal sealed class PreferenceConfiguration : IEntityTypeConfiguration<PreferenceEntity>
{
    public void Configure(EntityTypeBuilder<PreferenceEntity> builder)
    {
        builder.ToTable("Preferences");
        builder.HasKey(preference => preference.Id);
        builder.Property(preference => preference.Id).ValueGeneratedNever();
        builder.Property(preference => preference.PlaybackSpeed);
        builder.Property(preference => preference.Revision).IsConcurrencyToken();
        builder.ToTable(t => t.HasCheckConstraint("CK_Preferences_CacheLimitBytes", "CacheLimitBytes >= 0"));
    }
}