using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TutsVideoPlayer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SubtitleAvailabilityAndNormalizationSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SubtitleAssociations_SubtitleTracks_SubtitleTrackId",
                table: "SubtitleAssociations");

            migrationBuilder.DropIndex(
                name: "IX_SubtitleTracks_CourseId",
                table: "SubtitleTracks");

            migrationBuilder.AddColumn<int>(
                name: "Availability",
                table: "SubtitleTracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "NormalizedSourceLengthBytes",
                table: "SubtitleTracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NormalizedSourceModifiedUtcMs",
                table: "SubtitleTracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubtitleTracks_CourseId_Availability",
                table: "SubtitleTracks",
                columns: new[] { "CourseId", "Availability" });

            migrationBuilder.AddForeignKey(
                name: "FK_SubtitleAssociations_SubtitleTracks_SubtitleTrackId",
                table: "SubtitleAssociations",
                column: "SubtitleTrackId",
                principalTable: "SubtitleTracks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SubtitleAssociations_SubtitleTracks_SubtitleTrackId",
                table: "SubtitleAssociations");

            migrationBuilder.DropIndex(
                name: "IX_SubtitleTracks_CourseId_Availability",
                table: "SubtitleTracks");

            migrationBuilder.DropColumn(
                name: "Availability",
                table: "SubtitleTracks");

            migrationBuilder.DropColumn(
                name: "NormalizedSourceLengthBytes",
                table: "SubtitleTracks");

            migrationBuilder.DropColumn(
                name: "NormalizedSourceModifiedUtcMs",
                table: "SubtitleTracks");

            migrationBuilder.CreateIndex(
                name: "IX_SubtitleTracks_CourseId",
                table: "SubtitleTracks",
                column: "CourseId");

            migrationBuilder.AddForeignKey(
                name: "FK_SubtitleAssociations_SubtitleTracks_SubtitleTrackId",
                table: "SubtitleAssociations",
                column: "SubtitleTrackId",
                principalTable: "SubtitleTracks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
