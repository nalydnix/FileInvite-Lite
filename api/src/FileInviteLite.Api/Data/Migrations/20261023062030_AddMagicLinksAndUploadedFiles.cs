using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileInviteLite.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMagicLinksAndUploadedFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MagicLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MagicLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MagicLinks_DocumentRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "DocumentRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UploadedFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChecklistItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadedFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UploadedFiles_ChecklistItems_ChecklistItemId",
                        column: x => x.ChecklistItemId,
                        principalTable: "ChecklistItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinks_RequestId",
                table: "MagicLinks",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_MagicLinks_TokenHash",
                table: "MagicLinks",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UploadedFiles_ChecklistItemId",
                table: "UploadedFiles",
                column: "ChecklistItemId");

            migrationBuilder.CreateIndex(
                name: "IX_UploadedFiles_Sha256",
                table: "UploadedFiles",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_UploadedFiles_StorageKey",
                table: "UploadedFiles",
                column: "StorageKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MagicLinks");

            migrationBuilder.DropTable(
                name: "UploadedFiles");
        }
    }
}
