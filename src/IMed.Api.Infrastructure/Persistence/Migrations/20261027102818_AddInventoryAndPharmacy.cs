using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryAndPharmacy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inventory_settings",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    adjustment_approval_threshold = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    expiry_alert_days = table.Column<int>(type: "integer", nullable: false),
                    walk_in_party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_settings", x => x.organization_id);
                });

            migrationBuilder.CreateTable(
                name: "stock_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    barcode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    base_unit = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    requires_lot = table.Column<bool>(type: "boolean", nullable: false),
                    sellable = table.Column<bool>(type: "boolean", nullable: false),
                    over_the_counter = table.Column<bool>(type: "boolean", nullable: false),
                    tax_code_id = table.Column<Guid>(type: "uuid", nullable: true),
                    billable_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    minimum_stock = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_item", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stock_return_record",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    returned_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_return_record", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stock_supplier",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tax_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    contact_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_supplier", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stock_warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_general = table.Column<bool>(type: "boolean", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_warehouse", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stock_item_medication",
                columns: table => new
                {
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    atc_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    active_ingredient = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    concentration = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    dose_form = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    route = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    sanitary_registration = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_item_medication", x => x.stock_item_id);
                    table.ForeignKey(
                        name: "FK_stock_item_medication_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_item_presentation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    factor_to_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_item_presentation", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_item_presentation_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_item_reagent",
                columns: table => new
                {
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_conditions = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    open_stability_days = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_item_reagent", x => x.stock_item_id);
                    table.ForeignKey(
                        name: "FK_stock_item_reagent_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_item_supply",
                columns: table => new
                {
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sterile = table.Column<bool>(type: "boolean", nullable: false),
                    single_use = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_item_supply", x => x.stock_item_id);
                    table.ForeignKey(
                        name: "FK_stock_item_supply_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_lot",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_untracked = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_lot", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_lot_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pharmacy_dispense",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    patient_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    encounter_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    buyer_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    buyer_tax_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    dispensed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    dispensed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pharmacy_dispense", x => x.id);
                    table.ForeignKey(
                        name: "FK_pharmacy_dispense_stock_warehouse_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "stock_warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_adjustment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    value = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    stock_count_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_adjustment", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_adjustment_stock_warehouse_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "stock_warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_count",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_by = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    adjustment_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_count", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_count_stock_warehouse_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "stock_warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_goods_receipt",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supplier_invoice_number = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    supplier_invoice_date = table.Column<DateOnly>(type: "date", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_by = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    voided = table.Column<bool>(type: "boolean", nullable: false),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    voided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_goods_receipt", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_goods_receipt_stock_supplier_supplier_id",
                        column: x => x.supplier_id,
                        principalTable: "stock_supplier",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_goods_receipt_stock_warehouse_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "stock_warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_internal_consumption",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    area = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_internal_consumption", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_internal_consumption_stock_warehouse_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "stock_warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_item_cost",
                columns: table => new
                {
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity_on_hand = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    average_cost = table.Column<decimal>(type: "numeric(19,6)", precision: 19, scale: 6, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_item_cost", x => new { x.warehouse_id, x.stock_item_id });
                    table.CheckConstraint("ck_stock_item_cost_non_negative", "quantity_on_hand >= 0 AND average_cost >= 0");
                    table.ForeignKey(
                        name: "FK_stock_item_cost_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_item_cost_stock_warehouse_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "stock_warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_transfer",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transferred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    transferred_by = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_transfer", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_transfer_stock_warehouse_from_warehouse_id",
                        column: x => x.from_warehouse_id,
                        principalTable: "stock_warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_transfer_stock_warehouse_to_warehouse_id",
                        column: x => x.to_warehouse_id,
                        principalTable: "stock_warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_balance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity_on_hand = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_balance", x => x.id);
                    table.CheckConstraint("ck_stock_balance_non_negative", "quantity_on_hand >= 0");
                    table.ForeignKey(
                        name: "FK_stock_balance_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_balance_stock_lot_stock_lot_id",
                        column: x => x.stock_lot_id,
                        principalTable: "stock_lot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_balance_stock_warehouse_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "stock_warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_movement",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,6)", precision: 19, scale: 6, nullable: false),
                    total_cost = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    quantity_after = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    average_cost_after = table.Column<decimal>(type: "numeric(19,6)", precision: 19, scale: 6, nullable: false),
                    source_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    performed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reversal_of_movement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_movement", x => x.id);
                    table.CheckConstraint("ck_stock_movement_direction", "direction IN (1, -1)");
                    table.CheckConstraint("ck_stock_movement_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_stock_movement_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_movement_stock_lot_stock_lot_id",
                        column: x => x.stock_lot_id,
                        principalTable: "stock_lot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_movement_stock_warehouse_warehouse_id",
                        column: x => x.warehouse_id,
                        principalTable: "stock_warehouse",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pharmacy_dispense_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispense_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    medication_request_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prescribed_atc_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    prescribed_text = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    prescribed_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    prescribed_unit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    completes_prescription = table.Column<bool>(type: "boolean", nullable: false),
                    substitution_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    charge_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pharmacy_dispense_line", x => x.id);
                    table.CheckConstraint("ck_pharmacy_dispense_line_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_pharmacy_dispense_line_pharmacy_dispense_dispense_id",
                        column: x => x.dispense_id,
                        principalTable: "pharmacy_dispense",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_pharmacy_dispense_line_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_adjustment_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,6)", precision: 19, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_adjustment_line", x => x.id);
                    table.CheckConstraint("ck_stock_adjustment_line_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_stock_adjustment_line_stock_adjustment_document_id",
                        column: x => x.document_id,
                        principalTable: "stock_adjustment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_stock_adjustment_line_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_adjustment_line_stock_lot_stock_lot_id",
                        column: x => x.stock_lot_id,
                        principalTable: "stock_lot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_count_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_count_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expected_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    counted_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_count_line", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_count_line_stock_count_stock_count_id",
                        column: x => x.stock_count_id,
                        principalTable: "stock_count",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_stock_count_line_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_count_line_stock_lot_stock_lot_id",
                        column: x => x.stock_lot_id,
                        principalTable: "stock_lot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_goods_receipt_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    goods_receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    presentation_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    presentation_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    factor_to_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,6)", precision: 19, scale: 6, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_goods_receipt_line", x => x.id);
                    table.ForeignKey(
                        name: "FK_stock_goods_receipt_line_stock_goods_receipt_goods_receipt_~",
                        column: x => x.goods_receipt_id,
                        principalTable: "stock_goods_receipt",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_stock_goods_receipt_line_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_goods_receipt_line_stock_lot_stock_lot_id",
                        column: x => x.stock_lot_id,
                        principalTable: "stock_lot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_internal_consumption_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,6)", precision: 19, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_internal_consumption_line", x => x.id);
                    table.CheckConstraint("ck_stock_internal_consumption_line_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_stock_internal_consumption_line_stock_internal_consumption_~",
                        column: x => x.document_id,
                        principalTable: "stock_internal_consumption",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_stock_internal_consumption_line_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_internal_consumption_line_stock_lot_stock_lot_id",
                        column: x => x.stock_lot_id,
                        principalTable: "stock_lot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_transfer_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,6)", precision: 19, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_transfer_line", x => x.id);
                    table.CheckConstraint("ck_stock_transfer_line_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_stock_transfer_line_stock_item_stock_item_id",
                        column: x => x.stock_item_id,
                        principalTable: "stock_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_transfer_line_stock_lot_stock_lot_id",
                        column: x => x.stock_lot_id,
                        principalTable: "stock_lot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_transfer_line_stock_transfer_document_id",
                        column: x => x.document_id,
                        principalTable: "stock_transfer",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pharmacy_dispense_lot",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispense_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(19,6)", precision: 19, scale: 6, nullable: false),
                    returned_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pharmacy_dispense_lot", x => x.id);
                    table.CheckConstraint("ck_pharmacy_dispense_lot_returned", "returned_quantity >= 0 AND returned_quantity <= quantity");
                    table.ForeignKey(
                        name: "FK_pharmacy_dispense_lot_pharmacy_dispense_line_dispense_line_~",
                        column: x => x.dispense_line_id,
                        principalTable: "pharmacy_dispense_line",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_pharmacy_dispense_lot_stock_lot_stock_lot_id",
                        column: x => x.stock_lot_id,
                        principalTable: "stock_lot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pharmacy_dispense_org_encounter",
                table: "pharmacy_dispense",
                columns: new[] { "organization_id", "encounter_fhir_id" },
                filter: "encounter_fhir_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pharmacy_dispense_org_location_date",
                table: "pharmacy_dispense",
                columns: new[] { "organization_id", "location_id", "dispensed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_pharmacy_dispense_warehouse_id",
                table: "pharmacy_dispense",
                column: "warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ux_pharmacy_dispense_org_key",
                table: "pharmacy_dispense",
                columns: new[] { "organization_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pharmacy_dispense_line_dispense_id",
                table: "pharmacy_dispense_line",
                column: "dispense_id");

            migrationBuilder.CreateIndex(
                name: "ix_pharmacy_dispense_line_mr",
                table: "pharmacy_dispense_line",
                column: "medication_request_fhir_id");

            migrationBuilder.CreateIndex(
                name: "IX_pharmacy_dispense_line_stock_item_id",
                table: "pharmacy_dispense_line",
                column: "stock_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_pharmacy_dispense_lot_dispense_line_id",
                table: "pharmacy_dispense_lot",
                column: "dispense_line_id");

            migrationBuilder.CreateIndex(
                name: "IX_pharmacy_dispense_lot_stock_lot_id",
                table: "pharmacy_dispense_lot",
                column: "stock_lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_adjustment_org_status",
                table: "stock_adjustment",
                columns: new[] { "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_adjustment_warehouse_id",
                table: "stock_adjustment",
                column: "warehouse_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_adjustment_line_document_id",
                table: "stock_adjustment_line",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_adjustment_line_stock_item_id",
                table: "stock_adjustment_line",
                column: "stock_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_adjustment_line_stock_lot_id",
                table: "stock_adjustment_line",
                column: "stock_lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_balance_org_item",
                table: "stock_balance",
                columns: new[] { "organization_id", "stock_item_id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_balance_stock_item_id",
                table: "stock_balance",
                column: "stock_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_balance_stock_lot_id",
                table: "stock_balance",
                column: "stock_lot_id");

            migrationBuilder.CreateIndex(
                name: "ux_stock_balance_warehouse_lot",
                table: "stock_balance",
                columns: new[] { "warehouse_id", "stock_lot_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_stock_count_open_per_warehouse",
                table: "stock_count",
                column: "warehouse_id",
                unique: true,
                filter: "status = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "IX_stock_count_line_stock_count_id",
                table: "stock_count_line",
                column: "stock_count_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_count_line_stock_item_id",
                table: "stock_count_line",
                column: "stock_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_count_line_stock_lot_id",
                table: "stock_count_line",
                column: "stock_lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_goods_receipt_org_date",
                table: "stock_goods_receipt",
                columns: new[] { "organization_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_goods_receipt_supplier_id",
                table: "stock_goods_receipt",
                column: "supplier_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_goods_receipt_warehouse_id",
                table: "stock_goods_receipt",
                column: "warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ux_stock_goods_receipt_supplier_invoice",
                table: "stock_goods_receipt",
                columns: new[] { "organization_id", "supplier_id", "supplier_invoice_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_goods_receipt_line_goods_receipt_id",
                table: "stock_goods_receipt_line",
                column: "goods_receipt_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_goods_receipt_line_stock_item_id",
                table: "stock_goods_receipt_line",
                column: "stock_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_goods_receipt_line_stock_lot_id",
                table: "stock_goods_receipt_line",
                column: "stock_lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_internal_consumption_org_date",
                table: "stock_internal_consumption",
                columns: new[] { "organization_id", "consumed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_internal_consumption_warehouse_id",
                table: "stock_internal_consumption",
                column: "warehouse_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_internal_consumption_line_document_id",
                table: "stock_internal_consumption_line",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_internal_consumption_line_stock_item_id",
                table: "stock_internal_consumption_line",
                column: "stock_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_internal_consumption_line_stock_lot_id",
                table: "stock_internal_consumption_line",
                column: "stock_lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_item_org_active_name",
                table: "stock_item",
                columns: new[] { "organization_id", "active", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_item_org_barcode",
                table: "stock_item",
                columns: new[] { "organization_id", "barcode" },
                filter: "barcode IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_stock_item_org_code",
                table: "stock_item",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_item_cost_org",
                table: "stock_item_cost",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_item_cost_stock_item_id",
                table: "stock_item_cost",
                column: "stock_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_item_medication_atc",
                table: "stock_item_medication",
                column: "atc_code");

            migrationBuilder.CreateIndex(
                name: "IX_stock_item_presentation_stock_item_id",
                table: "stock_item_presentation",
                column: "stock_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_lot_org_expiry",
                table: "stock_lot",
                columns: new[] { "organization_id", "expiry_date" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_lot_item_code",
                table: "stock_lot",
                columns: new[] { "stock_item_id", "lot_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_movement_kardex",
                table: "stock_movement",
                columns: new[] { "warehouse_id", "stock_item_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movement_org_date",
                table: "stock_movement",
                columns: new[] { "organization_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movement_source",
                table: "stock_movement",
                columns: new[] { "source_kind", "source_id" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_movement_stock_item_id",
                table: "stock_movement",
                column: "stock_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_movement_stock_lot_id",
                table: "stock_movement",
                column: "stock_lot_id");

            migrationBuilder.CreateIndex(
                name: "ux_stock_return_credit_note_charge",
                table: "stock_return_record",
                columns: new[] { "organization_id", "credit_note_id", "charge_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_supplier_org_name",
                table: "stock_supplier",
                columns: new[] { "organization_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_supplier_org_tax_id",
                table: "stock_supplier",
                columns: new[] { "organization_id", "tax_id" },
                unique: true,
                filter: "tax_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfer_from_warehouse_id",
                table: "stock_transfer",
                column: "from_warehouse_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_transfer_org_date",
                table: "stock_transfer",
                columns: new[] { "organization_id", "transferred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfer_to_warehouse_id",
                table: "stock_transfer",
                column: "to_warehouse_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfer_line_document_id",
                table: "stock_transfer_line",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfer_line_stock_item_id",
                table: "stock_transfer_line",
                column: "stock_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_stock_transfer_line_stock_lot_id",
                table: "stock_transfer_line",
                column: "stock_lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_warehouse_org_location",
                table: "stock_warehouse",
                columns: new[] { "organization_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ux_stock_warehouse_general_per_location",
                table: "stock_warehouse",
                column: "location_id",
                unique: true,
                filter: "is_general");

            migrationBuilder.CreateIndex(
                name: "ux_stock_warehouse_org_code",
                table: "stock_warehouse",
                columns: new[] { "organization_id", "code" },
                unique: true);

            // 🔴 El libro de movimientos es append-only (FSF §8): un error se corrige con otro
            // movimiento, nunca editando o borrando una fila. El código no tiene cómo hacerlo, y la
            // base lo impide igual para cualquiera que llegue por otro lado (un script, una consola).
            // ⚠️ SQL a mano: si esta migración se regenera, hay que volver a agregar este bloque y
            // su contraparte en Down.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION stock_movement_append_only() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'stock_movement es append-only: un movimiento no se edita ni se borra; se corrige con otro movimiento.';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_stock_movement_append_only
                    BEFORE UPDATE OR DELETE ON stock_movement
                    FOR EACH ROW EXECUTE FUNCTION stock_movement_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_stock_movement_append_only ON stock_movement;
                DROP FUNCTION IF EXISTS stock_movement_append_only();
                """);

            migrationBuilder.DropTable(
                name: "inventory_settings");

            migrationBuilder.DropTable(
                name: "pharmacy_dispense_lot");

            migrationBuilder.DropTable(
                name: "stock_adjustment_line");

            migrationBuilder.DropTable(
                name: "stock_balance");

            migrationBuilder.DropTable(
                name: "stock_count_line");

            migrationBuilder.DropTable(
                name: "stock_goods_receipt_line");

            migrationBuilder.DropTable(
                name: "stock_internal_consumption_line");

            migrationBuilder.DropTable(
                name: "stock_item_cost");

            migrationBuilder.DropTable(
                name: "stock_item_medication");

            migrationBuilder.DropTable(
                name: "stock_item_presentation");

            migrationBuilder.DropTable(
                name: "stock_item_reagent");

            migrationBuilder.DropTable(
                name: "stock_item_supply");

            migrationBuilder.DropTable(
                name: "stock_movement");

            migrationBuilder.DropTable(
                name: "stock_return_record");

            migrationBuilder.DropTable(
                name: "stock_transfer_line");

            migrationBuilder.DropTable(
                name: "pharmacy_dispense_line");

            migrationBuilder.DropTable(
                name: "stock_adjustment");

            migrationBuilder.DropTable(
                name: "stock_count");

            migrationBuilder.DropTable(
                name: "stock_goods_receipt");

            migrationBuilder.DropTable(
                name: "stock_internal_consumption");

            migrationBuilder.DropTable(
                name: "stock_lot");

            migrationBuilder.DropTable(
                name: "stock_transfer");

            migrationBuilder.DropTable(
                name: "pharmacy_dispense");

            migrationBuilder.DropTable(
                name: "stock_supplier");

            migrationBuilder.DropTable(
                name: "stock_item");

            migrationBuilder.DropTable(
                name: "stock_warehouse");
        }
    }
}
