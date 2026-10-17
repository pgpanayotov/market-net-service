using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace MarketData.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "symbols",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:IdentitySequenceOptions", "'100', '1', '', '', 'False', '1'")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_symbols", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "candles_1m",
                columns: table => new
                {
                    symbol_id = table.Column<int>(type: "integer", nullable: false),
                    open_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    open = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    high = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    low = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    close = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    volume = table.Column<decimal>(type: "numeric(38,10)", precision: 38, scale: 10, nullable: false),
                    quote_volume = table.Column<decimal>(type: "numeric(38,10)", precision: 38, scale: 10, nullable: false),
                    trade_count = table.Column<int>(type: "integer", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candles_1m", x => new { x.symbol_id, x.open_time });
                    table.ForeignKey(
                        name: "fk_candles_1m_symbols_symbol_id",
                        column: x => x.symbol_id,
                        principalTable: "symbols",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "symbols",
                columns: new[] { "id", "created_at", "is_active", "name", "updated_at" },
                values: new object[,]
                {
                    { 1, new DateTimeOffset(new DateTime(2026, 10, 18, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, "BTCUSDT", new DateTimeOffset(new DateTime(2026, 10, 18, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { 2, new DateTimeOffset(new DateTime(2026, 10, 18, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, "ADAUSDT", new DateTimeOffset(new DateTime(2026, 10, 18, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { 3, new DateTimeOffset(new DateTime(2026, 10, 18, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, "ETHUSDT", new DateTimeOffset(new DateTime(2026, 10, 18, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });

            migrationBuilder.CreateIndex(
                name: "ix_symbols_name",
                table: "symbols",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candles_1m");

            migrationBuilder.DropTable(
                name: "symbols");
        }
    }
}
