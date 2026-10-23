using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdempotentPayments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ClaimedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "IdempotencyKeys",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.Sql("UPDATE \"IdempotencyKeys\" SET \"ClaimedAt\" = \"CreatedAt\";");

            migrationBuilder.AlterColumn<DateTime>(
                name: "ClaimedAt",
                table: "IdempotencyKeys",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "IdempotencyKeys");
        }
    }
}
