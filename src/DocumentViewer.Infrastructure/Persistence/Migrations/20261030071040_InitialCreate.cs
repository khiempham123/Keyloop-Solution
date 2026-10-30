using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DocumentViewer.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_metadata_cache",
                columns: table => new
                {
                    vin = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: false),
                    source_system = table.Column<short>(type: "smallint", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    document_count = table.Column<int>(type: "integer", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_metadata_cache", x => new { x.vin, x.source_system });
                });

            migrationBuilder.CreateTable(
                name: "search_audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    vin = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    searched_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    partial = table.Column<bool>(type: "boolean", nullable: false),
                    source_report = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_audit", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_metadata_cache_expires_at",
                table: "document_metadata_cache",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_search_audit_vin_searched_at",
                table: "search_audit",
                columns: new[] { "vin", "searched_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_metadata_cache");

            migrationBuilder.DropTable(
                name: "search_audit");
        }
    }
}
