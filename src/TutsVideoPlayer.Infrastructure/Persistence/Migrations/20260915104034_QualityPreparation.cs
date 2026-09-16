using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TutsVideoPlayer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QualityPreparation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ReservedBytes",
                table: "PreparationJobs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReservedBytes",
                table: "PreparationJobs");
        }
    }
}
