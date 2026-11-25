using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Umbraco.Community.FormsAuditTrail.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddEntryAccessAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "formsAuditTrailAccess",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    FormId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FormName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    RecordId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RecordCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UserKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_formsAuditTrailAccess", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "formsAuditTrailState",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_formsAuditTrailState", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "formsAuditTrailAccessRecords",
                columns: table => new
                {
                    AccessId = table.Column<long>(type: "INTEGER", nullable: false),
                    RecordId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_formsAuditTrailAccessRecords", x => new { x.AccessId, x.RecordId });
                    table.ForeignKey(
                        name: "FK_formsAuditTrailAccessRecords_formsAuditTrailAccess_AccessId",
                        column: x => x.AccessId,
                        principalTable: "formsAuditTrailAccess",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "formsAuditTrailEntryChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccessId = table.Column<long>(type: "INTEGER", nullable: false),
                    FieldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FieldCaption = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    OldValue = table.Column<string>(type: "TEXT", nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", nullable: true),
                    Masked = table.Column<bool>(type: "INTEGER", nullable: false),
                    Cleared = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ValuesScrubbedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_formsAuditTrailEntryChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_formsAuditTrailEntryChanges_formsAuditTrailAccess_AccessId",
                        column: x => x.AccessId,
                        principalTable: "formsAuditTrailAccess",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_formsAuditTrailAccess_CorrelationId",
                table: "formsAuditTrailAccess",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_formsAuditTrailAccess_FormId",
                table: "formsAuditTrailAccess",
                column: "FormId");

            migrationBuilder.CreateIndex(
                name: "IX_formsAuditTrailAccess_FormId_Timestamp",
                table: "formsAuditTrailAccess",
                columns: new[] { "FormId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_formsAuditTrailAccess_RecordId",
                table: "formsAuditTrailAccess",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_formsAuditTrailAccess_Timestamp",
                table: "formsAuditTrailAccess",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_formsAuditTrailAccess_UserKey",
                table: "formsAuditTrailAccess",
                column: "UserKey");

            migrationBuilder.CreateIndex(
                name: "IX_formsAuditTrailAccessRecords_RecordId",
                table: "formsAuditTrailAccessRecords",
                column: "RecordId");

            migrationBuilder.CreateIndex(
                name: "IX_formsAuditTrailEntryChanges_AccessId",
                table: "formsAuditTrailEntryChanges",
                column: "AccessId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "formsAuditTrailAccessRecords");

            migrationBuilder.DropTable(
                name: "formsAuditTrailEntryChanges");

            migrationBuilder.DropTable(
                name: "formsAuditTrailState");

            migrationBuilder.DropTable(
                name: "formsAuditTrailAccess");
        }
    }
}
