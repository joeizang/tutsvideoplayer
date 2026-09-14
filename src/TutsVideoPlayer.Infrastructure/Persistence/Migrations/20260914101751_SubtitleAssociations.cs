using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TutsVideoPlayer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubtitleAssociations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "SubtitleTracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizationVersion",
                table: "SubtitleTracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedRelativePath",
                table: "SubtitleTracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParseError",
                table: "SubtitleTracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SubtitleAssociations",
                columns: table => new
                {
                    LessonId = table.Column<long>(type: "INTEGER", nullable: false),
                    SubtitleTrackId = table.Column<long>(type: "INTEGER", nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceGeneration = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubtitleAssociations", x => new { x.LessonId, x.SubtitleTrackId });
                    table.ForeignKey(
                        name: "FK_SubtitleAssociations_Lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "Lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SubtitleAssociations_SubtitleTracks_SubtitleTrackId",
                        column: x => x.SubtitleTrackId,
                        principalTable: "SubtitleTracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubtitleAssociations_SubtitleTrackId",
                table: "SubtitleAssociations",
                column: "SubtitleTrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubtitleAssociations");

            migrationBuilder.DropColumn(
                name: "Fingerprint",
                table: "SubtitleTracks");

            migrationBuilder.DropColumn(
                name: "NormalizationVersion",
                table: "SubtitleTracks");

            migrationBuilder.DropColumn(
                name: "NormalizedRelativePath",
                table: "SubtitleTracks");

            migrationBuilder.DropColumn(
                name: "ParseError",
                table: "SubtitleTracks");
        }
    }
}
