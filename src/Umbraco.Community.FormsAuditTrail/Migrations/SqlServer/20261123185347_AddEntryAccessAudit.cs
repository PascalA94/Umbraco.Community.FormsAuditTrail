using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Umbraco.Community.FormsAuditTrail.Migrations.SqlServer
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    FormId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FormName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordCount = table.Column<int>(type: "int", nullable: false),
                    UserKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_formsAuditTrailAccess", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "formsAuditTrailState",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_formsAuditTrailState", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "formsAuditTrailAccessRecords",
                columns: table => new
                {
                    AccessId = table.Column<long>(type: "bigint", nullable: false),
                    RecordId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccessId = table.Column<long>(type: "bigint", nullable: false),
                    FieldId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FieldCaption = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Masked = table.Column<bool>(type: "bit", nullable: false),
                    Cleared = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ValuesScrubbedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
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
