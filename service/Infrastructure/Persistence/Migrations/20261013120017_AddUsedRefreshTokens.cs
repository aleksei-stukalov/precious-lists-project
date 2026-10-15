using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace service.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsedRefreshTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UsedRefreshTokens",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsedRefreshTokens", x => x.Hash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsedRefreshTokens_ExpiresAt",
                table: "UsedRefreshTokens",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsedRefreshTokens");
        }
    }
}
