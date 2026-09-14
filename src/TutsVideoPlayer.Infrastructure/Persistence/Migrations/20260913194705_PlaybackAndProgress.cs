using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TutsVideoPlayer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlaybackAndProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LessonProgress",
                columns: table => new
                {
                    LessonId = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    PositionMs = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxObservedPositionMs = table.Column<long>(type: "INTEGER", nullable: false),
                    AutomaticCompleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ManualCompletion = table.Column<int>(type: "INTEGER", nullable: true),
                    ActiveSessionId = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    LastWatchedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonProgress", x => new { x.LessonId, x.SourceGeneration });
                    table.CheckConstraint("CK_LessonProgress_LastSequence", "LastSequence >= 0");
                    table.CheckConstraint("CK_LessonProgress_PositionMs", "PositionMs >= 0");
                    table.ForeignKey(
                        name: "FK_LessonProgress_Lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "Lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlaybackSessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LessonId = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    LastHeartbeatUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ActiveRenditionId = table.Column<long>(type: "INTEGER", nullable: true),
                    ClosedUtcMs = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaybackSessions_Lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "Lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Preferences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    PreferredQuality = table.Column<string>(type: "TEXT", nullable: true),
                    PlaybackSpeed = table.Column<double>(type: "REAL", nullable: false),
                    SubtitleEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreferredLanguage = table.Column<string>(type: "TEXT", nullable: true),
                    Autoplay = table.Column<bool>(type: "INTEGER", nullable: false),
                    FitMode = table.Column<string>(type: "TEXT", nullable: false),
                    CacheLimitBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    QueuePaused = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Preferences", x => x.Id);
                    table.CheckConstraint("CK_Preferences_CacheLimitBytes", "CacheLimitBytes >= 0");
                });

            migrationBuilder.CreateTable(
                name: "Renditions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LessonId = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    Purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    Profile = table.Column<string>(type: "TEXT", nullable: true),
                    RecipeVersion = table.Column<string>(type: "TEXT", nullable: true),
                    RetentionClass = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    ManifestPath = table.Column<string>(type: "TEXT", nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    VideoCodec = table.Column<string>(type: "TEXT", nullable: true),
                    AudioCodec = table.Column<string>(type: "TEXT", nullable: true),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputHash = table.Column<string>(type: "TEXT", nullable: true),
                    LastAccessUtcMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Renditions", x => x.Id);
                    table.CheckConstraint("CK_Renditions_ByteLength", "ByteLength >= 0");
                    table.ForeignKey(
                        name: "FK_Renditions_Lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "Lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LessonProgress_LastWatchedUtcMs",
                table: "LessonProgress",
                column: "LastWatchedUtcMs");

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackSessions_LastHeartbeatUtcMs",
                table: "PlaybackSessions",
                column: "LastHeartbeatUtcMs");

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackSessions_LessonId_LastHeartbeatUtcMs",
                table: "PlaybackSessions",
                columns: new[] { "LessonId", "LastHeartbeatUtcMs" });

            migrationBuilder.CreateIndex(
                name: "IX_Renditions_LessonId_SourceGeneration_Purpose_Profile_RecipeVersion",
                table: "Renditions",
                columns: new[] { "LessonId", "SourceGeneration", "Purpose", "Profile", "RecipeVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Renditions_RelativePath",
                table: "Renditions",
                column: "RelativePath");

            migrationBuilder.CreateIndex(
                name: "IX_Renditions_Status_RetentionClass",
                table: "Renditions",
                columns: new[] { "Status", "RetentionClass" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LessonProgress");

            migrationBuilder.DropTable(
                name: "PlaybackSessions");

            migrationBuilder.DropTable(
                name: "Preferences");

            migrationBuilder.DropTable(
                name: "Renditions");
        }
    }
}
