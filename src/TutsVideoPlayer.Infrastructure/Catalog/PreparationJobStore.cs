using Microsoft.EntityFrameworkCore;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Infrastructure.Persistence.Entities;

namespace TutsVideoPlayer.Infrastructure.Catalog;

public static class PreparationJobStore
{
    public static async Task<PreparationJobEntity> GetOrCreateAsync(AppDbContext context, PreparationJobEntity job, CancellationToken token)
    {
        // SQLite resolves simultaneous first creation in the unique index, rather than
        // requiring a read-before-insert race and exception-driven HTTP errors.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO PreparationJobs
            (LessonId, SourceGeneration, DedupKey, Purpose, Profile, RecipeVersion, State, Priority, EnqueuedUtcMs, Attempt, Revision, ErrorCode, UserMessage)
            VALUES ({job.LessonId}, {job.SourceGeneration}, {job.DedupKey}, {(int)job.Purpose}, {job.Profile}, {job.RecipeVersion}, {(int)job.State}, {job.Priority}, {job.EnqueuedUtcMs}, 0, 0, {job.ErrorCode}, {job.UserMessage})
            ON CONFLICT(DedupKey) DO NOTHING
            """, token);
        return await context.PreparationJobs.SingleAsync(candidate => candidate.DedupKey == job.DedupKey, token);
    }
}
