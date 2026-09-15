using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TutsVideoPlayer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreparationJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PreparationJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LessonId = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    DedupKey = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    Profile = table.Column<string>(type: "TEXT", nullable: true),
                    RecipeVersion = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    EnqueuedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedUtcMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseExpiresUtcMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Progress = table.Column<double>(type: "REAL", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    UserMessage = table.Column<string>(type: "TEXT", nullable: true),
                    OutputRelativePath = table.Column<string>(type: "TEXT", nullable: true),
                    ManifestRelativePath = table.Column<string>(type: "TEXT", nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreparationJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreparationJobs_Lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "Lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PreparationAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedUtcMs = table.Column<long>(type: "INTEGER", nullable: true),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    SanitizedFailure = table.Column<string>(type: "TEXT", nullable: true),
                    TempRelativePath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreparationAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreparationAttempts_PreparationJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "PreparationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PreparationAttempts_JobId_StartedUtcMs",
                table: "PreparationAttempts",
                columns: new[] { "JobId", "StartedUtcMs" });

            migrationBuilder.CreateIndex(
                name: "IX_PreparationJobs_DedupKey",
                table: "PreparationJobs",
                column: "DedupKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreparationJobs_LeaseExpiresUtcMs",
                table: "PreparationJobs",
                column: "LeaseExpiresUtcMs");

            migrationBuilder.CreateIndex(
                name: "IX_PreparationJobs_LessonId",
                table: "PreparationJobs",
                column: "LessonId");

            migrationBuilder.CreateIndex(
                name: "IX_PreparationJobs_State_Priority_EnqueuedUtcMs_Id",
                table: "PreparationJobs",
                columns: new[] { "State", "Priority", "EnqueuedUtcMs", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PreparationAttempts");

            migrationBuilder.DropTable(
                name: "PreparationJobs");
        }
    }
}
