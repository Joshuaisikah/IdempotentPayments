using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IdempotentPayments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyKeyPaymentIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IdempotencyKeys_PaymentId",
                table: "IdempotencyKeys",
                column: "PaymentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IdempotencyKeys_PaymentId",
                table: "IdempotencyKeys");
        }
    }
}
