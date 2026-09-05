// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Sarafan.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackofficeIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backoffice_roles",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    display_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backoffice_roles", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "backoffice_users",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    patronymic = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    password_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_demo = table.Column<bool>(type: "boolean", nullable: false),
                    token_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backoffice_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "backoffice_refresh_sessions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    backoffice_user_id = table.Column<int>(type: "integer", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    replaced_by_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backoffice_refresh_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_backoffice_refresh_sessions_backoffice_users_backoffice_use~",
                        column: x => x.backoffice_user_id,
                        principalTable: "backoffice_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "backoffice_user_roles",
                columns: table => new
                {
                    backoffice_user_id = table.Column<int>(type: "integer", nullable: false),
                    role_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backoffice_user_roles", x => new { x.backoffice_user_id, x.role_code });
                    table.ForeignKey(
                        name: "FK_backoffice_user_roles_backoffice_roles_role_code",
                        column: x => x.role_code,
                        principalTable: "backoffice_roles",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backoffice_user_roles_backoffice_users_backoffice_user_id",
                        column: x => x.backoffice_user_id,
                        principalTable: "backoffice_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "backoffice_roles",
                columns: new[] { "code", "display_name" },
                values: new object[,]
                {
                    { "administrator", "Administrator" },
                    { "operator", "Operator" },
                    { "senior-operator", "Senior operator" },
                    { "shift-manager", "Shift manager" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_backoffice_refresh_sessions_backoffice_user_id_family_id",
                table: "backoffice_refresh_sessions",
                columns: new[] { "backoffice_user_id", "family_id" });

            migrationBuilder.CreateIndex(
                name: "IX_backoffice_refresh_sessions_token_hash",
                table: "backoffice_refresh_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backoffice_user_roles_role_code",
                table: "backoffice_user_roles",
                column: "role_code");

            migrationBuilder.CreateIndex(
                name: "IX_backoffice_users_normalized_email",
                table: "backoffice_users",
                column: "normalized_email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backoffice_refresh_sessions");

            migrationBuilder.DropTable(
                name: "backoffice_user_roles");

            migrationBuilder.DropTable(
                name: "backoffice_roles");

            migrationBuilder.DropTable(
                name: "backoffice_users");
        }
    }
}
