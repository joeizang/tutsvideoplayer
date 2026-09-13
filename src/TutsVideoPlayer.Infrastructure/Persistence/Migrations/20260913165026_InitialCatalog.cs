using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TutsVideoPlayer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Libraries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    LogicalIdentity = table.Column<string>(type: "TEXT", nullable: false),
                    LastSuccessfulScanId = table.Column<long>(type: "INTEGER", nullable: true),
                    CatalogRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Libraries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScanRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedUtcMs = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    DiscoveredCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IssueCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CatalogRevision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Courses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: false),
                    RelativeDirectory = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayTitle = table.Column<string>(type: "TEXT", nullable: false),
                    SearchTitle = table.Column<string>(type: "TEXT", nullable: false),
                    SortKey = table.Column<string>(type: "TEXT", nullable: false),
                    Availability = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Courses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Courses_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScanIssues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScanRunId = table.Column<long>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: true),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanIssues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScanIssues_ScanRuns_ScanRunId",
                        column: x => x.ScanRunId,
                        principalTable: "ScanRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LessonFolders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CourseId = table.Column<long>(type: "INTEGER", nullable: false),
                    ParentFolderId = table.Column<long>(type: "INTEGER", nullable: true),
                    RelativeDirectory = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    SortKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LessonFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LessonFolders_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LessonFolders_LessonFolders_ParentFolderId",
                        column: x => x.ParentFolderId,
                        principalTable: "LessonFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubtitleTracks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CourseId = table.Column<long>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    Format = table.Column<string>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    LengthBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ParseStatus = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubtitleTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubtitleTracks_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Lessons",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CourseId = table.Column<long>(type: "INTEGER", nullable: false),
                    FolderId = table.Column<long>(type: "INTEGER", nullable: true),
                    SourceGeneration = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    PrimaryRelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    SearchTitle = table.Column<string>(type: "TEXT", nullable: false),
                    SortKey = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Availability = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenScanId = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lessons", x => x.Id);
                    table.CheckConstraint("CK_Lessons_DurationMs", "DurationMs IS NULL OR DurationMs > 0");
                    table.CheckConstraint("CK_Lessons_SourceGeneration", "SourceGeneration >= 1");
                    table.ForeignKey(
                        name: "FK_Lessons_Courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "Courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Lessons_LessonFolders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "LessonFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SourceComponents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LessonId = table.Column<long>(type: "INTEGER", nullable: false),
                    Generation = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    LengthBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: true),
                    ProbeMetadata = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceComponents", x => x.Id);
                    table.CheckConstraint("CK_SourceComponents_LengthBytes", "LengthBytes >= 0");
                    table.ForeignKey(
                        name: "FK_SourceComponents_Lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "Lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Courses_LibraryId_RelativeDirectory",
                table: "Courses",
                columns: new[] { "LibraryId", "RelativeDirectory" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Courses_LibraryId_SortKey",
                table: "Courses",
                columns: new[] { "LibraryId", "SortKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Courses_SearchTitle",
                table: "Courses",
                column: "SearchTitle");

            migrationBuilder.CreateIndex(
                name: "IX_LessonFolders_CourseId_RelativeDirectory",
                table: "LessonFolders",
                columns: new[] { "CourseId", "RelativeDirectory" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LessonFolders_CourseId_SortKey",
                table: "LessonFolders",
                columns: new[] { "CourseId", "SortKey" });

            migrationBuilder.CreateIndex(
                name: "IX_LessonFolders_ParentFolderId",
                table: "LessonFolders",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Lessons_CourseId_PrimaryRelativePath",
                table: "Lessons",
                columns: new[] { "CourseId", "PrimaryRelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Lessons_CourseId_SortKey",
                table: "Lessons",
                columns: new[] { "CourseId", "SortKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Lessons_FolderId",
                table: "Lessons",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Lessons_LastSeenScanId",
                table: "Lessons",
                column: "LastSeenScanId");

            migrationBuilder.CreateIndex(
                name: "IX_Libraries_LogicalIdentity",
                table: "Libraries",
                column: "LogicalIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScanIssues_ScanRunId",
                table: "ScanIssues",
                column: "ScanRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanRuns_State",
                table: "ScanRuns",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_SourceComponents_LessonId_Generation_Role_RelativePath",
                table: "SourceComponents",
                columns: new[] { "LessonId", "Generation", "Role", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceComponents_RelativePath",
                table: "SourceComponents",
                column: "RelativePath");

            migrationBuilder.CreateIndex(
                name: "IX_SubtitleTracks_CourseId",
                table: "SubtitleTracks",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_SubtitleTracks_RelativePath",
                table: "SubtitleTracks",
                column: "RelativePath",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScanIssues");

            migrationBuilder.DropTable(
                name: "SourceComponents");

            migrationBuilder.DropTable(
                name: "SubtitleTracks");

            migrationBuilder.DropTable(
                name: "ScanRuns");

            migrationBuilder.DropTable(
                name: "Lessons");

            migrationBuilder.DropTable(
                name: "LessonFolders");

            migrationBuilder.DropTable(
                name: "Courses");

            migrationBuilder.DropTable(
                name: "Libraries");
        }
    }
}
