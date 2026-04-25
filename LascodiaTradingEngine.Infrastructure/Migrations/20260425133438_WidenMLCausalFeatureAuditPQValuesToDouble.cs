using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WidenMLCausalFeatureAuditPQValuesToDouble : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "GrangerQValue",
                table: "MLCausalFeatureAudit",
                type: "double precision",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,8)",
                oldPrecision: 10,
                oldScale: 8);

            migrationBuilder.AlterColumn<double>(
                name: "GrangerPValue",
                table: "MLCausalFeatureAudit",
                type: "double precision",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,8)",
                oldPrecision: 10,
                oldScale: 8);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "GrangerQValue",
                table: "MLCausalFeatureAudit",
                type: "numeric(10,8)",
                precision: 10,
                scale: 8,
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AlterColumn<decimal>(
                name: "GrangerPValue",
                table: "MLCausalFeatureAudit",
                type: "numeric(10,8)",
                precision: 10,
                scale: 8,
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double precision");
        }
    }
}
