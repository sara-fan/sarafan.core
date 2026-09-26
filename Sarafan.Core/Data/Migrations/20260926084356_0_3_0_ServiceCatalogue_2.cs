using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sarafan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class _0_3_0_ServiceCatalogue_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_service_catalogue_service",
                table: "service_catalogue_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_catalogue_audit_service",
                table: "service_catalogue_audit_events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_service_catalogue_service",
                table: "service_catalogue_entries",
                sql: "service IN (0, 100, 200, 300, 400, 500, 600, 700)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_catalogue_audit_service",
                table: "service_catalogue_audit_events",
                sql: "service IN (0, 100, 200, 300, 400, 500, 600, 700)");
        }
    }
}
