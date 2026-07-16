using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Umbraco.Community.FormsAuditTrail.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "formsAuditTrailEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FormId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FormName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UserKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    BeforeSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    AfterSnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    ChangeSummaryJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_formsAuditTrailEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "formsAuditTrailChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AuditEntryId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChangeType = table.Column<int>(type: "INTEGER", nullable: false),
                    PropertyPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    FriendlyDescription = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    OldValue = table.Column<string>(type: "TEXT", nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_formsAuditTrailChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_formsAuditTrailChanges_formsAuditTrailEntries_AuditEntryId",
                        column: x => x.AuditEntryId,
                        principalTable: "formsAuditTrailEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_formsAuditTrailChanges_AuditEntryId",
                table: "formsAuditTrailChanges",
                column: "AuditEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_formsAuditTrailEntries_FormId",
                table: "formsAuditTrailEntries",
                column: "FormId");

            migrationBuilder.CreateIndex(
                name: "IX_formsAuditTrailEntries_Timestamp",
                table: "formsAuditTrailEntries",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_formsAuditTrailEntries_UserKey",
                table: "formsAuditTrailEntries",
                column: "UserKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "formsAuditTrailChanges");

            migrationBuilder.DropTable(
                name: "formsAuditTrailEntries");
        }
    }
}
