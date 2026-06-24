using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MerinoOne.SupplierPortal.Infrastructure.Persistence.Configurations;

public class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> b)
    {
        b.ApplyBaseEntityConvention("PurchaseOrder", "proc", "purchaseOrder");
        b.Property(x => x.PoNumber).HasColumnName("poNumber").HasMaxLength(50).IsRequired();
        b.Property(x => x.SupplierId).HasColumnName("supplierId");
        b.Property(x => x.BuyerUserId).HasColumnName("buyerUserId");
        b.Property(x => x.PoType).HasColumnName("poType").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.PoDate).HasColumnName("poDate").HasColumnType("datetime2");
        b.Property(x => x.PaymentTerms).HasColumnName("paymentTerms").HasMaxLength(200);
        b.Property(x => x.DeliveryTerms).HasColumnName("deliveryTerms").HasMaxLength(200);
        b.Property(x => x.DeliveryTermId).HasColumnName("deliveryTermId").HasColumnType("uniqueidentifier");
        b.Property(x => x.PaymentTermId).HasColumnName("paymentTermId").HasColumnType("uniqueidentifier");
        b.Property(x => x.PoStatus).HasColumnName("poStatus").HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.AcknowledgmentAt).HasColumnName("acknowledgmentAt").HasColumnType("datetime2");
        b.Property(x => x.AcceptedAt).HasColumnName("acceptedAt").HasColumnType("datetime2");
        b.Property(x => x.RejectionReason).HasColumnName("rejectionReason").HasMaxLength(1000);
        b.Property(x => x.ProposedDeliveryDate).HasColumnName("proposedDeliveryDate").HasColumnType("datetime2");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.ErpSyncId).HasColumnName("erpSyncId").HasMaxLength(100);
        b.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(2000);

        // R4 (2026-06-22) — Addendum A1: PO header currency FK + denormalized snapshot. ERP-owned.
        b.Property(x => x.CurrencyId).HasColumnName("currencyId").HasColumnType("uniqueidentifier");
        b.Property(x => x.CurrencyCode).HasColumnName("currencyCode").HasMaxLength(10);

        b.ToTable(t => t.HasCheckConstraint("CK_PurchaseOrder_poType", "[poType] IN ('Material','Service')"));

        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_PurchaseOrder_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.DeliveryTerm).WithMany().HasForeignKey(x => x.DeliveryTermId)
            .HasConstraintName("FK_PurchaseOrder_DeliveryTerm_DeliveryTermId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PaymentTerm).WithMany().HasForeignKey(x => x.PaymentTermId)
            .HasConstraintName("FK_PurchaseOrder_PaymentTerm_PaymentTermId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Currency).WithMany().HasForeignKey(x => x.CurrencyId)
            .HasConstraintName("FK_PurchaseOrder_Currency_CurrencyId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.PoNumber).HasDatabaseName("UQ_PurchaseOrder_poNumber").IsUnique();
        b.HasIndex(x => x.SupplierId).HasDatabaseName("IX_PurchaseOrder_supplierId");
        b.HasIndex(x => x.PoStatus).HasDatabaseName("IX_PurchaseOrder_poStatus");
        // Composite scope index — the always-on tenant + company business-data filter scans this path.
        b.HasIndex("TenantId", "TenantEntityId").HasDatabaseName("IX_PurchaseOrder_tenant_company");
        b.HasIndex(x => x.CurrencyId).HasDatabaseName("IX_PurchaseOrder_currencyId");
    }
}

public class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> b)
    {
        b.ApplyBaseEntityConvention("PurchaseOrderLine", "proc", "purchaseOrderLine");
        b.Property(x => x.PurchaseOrderId).HasColumnName("purchaseOrderId");
        b.Property(x => x.PositionNo).HasColumnName("positionNo");
        b.Property(x => x.SequenceNo).HasColumnName("sequenceNo");
        b.Property(x => x.ItemCode).HasColumnName("itemCode").HasMaxLength(50).IsRequired();
        b.Property(x => x.ItemDescription).HasColumnName("itemDescription").HasMaxLength(500);
        b.Property(x => x.ItemId).HasColumnName("itemId").HasColumnType("uniqueidentifier");
        b.Property(x => x.OrderUnit).HasColumnName("orderUnit").HasMaxLength(20);
        b.Property(x => x.OrderQty).HasColumnName("orderQty").HasColumnType("decimal(18,4)");
        b.Property(x => x.PriceUnit).HasColumnName("priceUnit").HasColumnType("decimal(18,4)");
        b.Property(x => x.Price).HasColumnName("price").HasColumnType("decimal(18,4)");
        b.Property(x => x.DiscountPct).HasColumnName("discountPct").HasColumnType("decimal(5,2)");
        b.Property(x => x.DiscountAmount).HasColumnName("discountAmount").HasColumnType("decimal(18,4)");
        b.Property(x => x.DeliveryDate).HasColumnName("deliveryDate").HasColumnType("datetime2");
        b.Property(x => x.TaxCode).HasColumnName("taxCode").HasMaxLength(20);
        b.Property(x => x.TaxDescription).HasColumnName("taxDescription").HasMaxLength(200);

        // R4 (2026-06-22) — Addendum A2: link taxCode to the proc.Tax master (keep the snapshot strings).
        b.Property(x => x.TaxId).HasColumnName("taxId").HasColumnType("uniqueidentifier");

        b.HasOne(x => x.PurchaseOrder).WithMany(p => p.Lines).HasForeignKey(x => x.PurchaseOrderId)
            .HasConstraintName("FK_PurchaseOrderLine_PurchaseOrder_PurchaseOrderId").OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId)
            .HasConstraintName("FK_PurchaseOrderLine_Item_ItemId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Tax).WithMany().HasForeignKey(x => x.TaxId)
            .HasConstraintName("FK_PurchaseOrderLine_Tax_TaxId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.TaxId).HasDatabaseName("IX_PurchaseOrderLine_taxId");
        // R4 (2026-06-24) — enforce the PO line natural key (PO, positionNo, sequenceNo). The inbound PO upsert
        // now keys lines on (positionNo, sequenceNo) — two lines may share positionNo with differing sequenceNo —
        // so this DB-level guard mirrors the app's effective uniqueness scope. Filtered on isDeleted=0 so a
        // soft-deleted line never blocks re-inserting the same (po, position, seq); matches the filtered-unique
        // pattern elsewhere in this codebase (UX_Payment_tenant_invoice_paymentReference, UQ_AsnPurchaseOrder_asn_po).
        b.HasIndex(x => new { x.PurchaseOrderId, x.PositionNo, x.SequenceNo })
            .HasDatabaseName("UX_PurchaseOrderLine_po_position_seq").IsUnique()
            .HasFilter("[isDeleted] = 0");
    }
}

// R4 (2026-06-24) — PO Negotiation. Aggregate root (seccode RLS + RowVersion + tenant scope), mirrors
// SupplierChangeRequest. Filtered-unique on (purchaseOrderId) for the open (Submitted) negotiation enforces
// one open negotiation per PO at the DB level. No PO-line mutation on approve — ERP re-syncs the revised PO.
public class PurchaseOrderNegotiationConfiguration : IEntityTypeConfiguration<PurchaseOrderNegotiation>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderNegotiation> b)
    {
        b.ApplyBaseEntityConvention("PurchaseOrderNegotiation", "proc", "purchaseOrderNegotiation");
        b.Property(x => x.PurchaseOrderId).HasColumnName("purchaseOrderId");
        b.Property(x => x.PoNumber).HasColumnName("poNumber").HasMaxLength(50).IsRequired();
        b.Property(x => x.SupplierId).HasColumnName("supplierId");
        b.Property(x => x.NegotiationStatus).HasColumnName("negotiationStatus").HasConversion<string>()
            .HasMaxLength(30).IsRequired();
        b.Property(x => x.PreviousPoStatus).HasColumnName("previousPoStatus").HasConversion<string>()
            .HasMaxLength(30).IsRequired();
        b.Property(x => x.SubmittedAt).HasColumnName("submittedAt").HasColumnType("datetime2");
        b.Property(x => x.ReviewedAt).HasColumnName("reviewedAt").HasColumnType("datetime2");
        b.Property(x => x.ReviewedBy).HasColumnName("reviewedBy").HasMaxLength(100);
        b.Property(x => x.RejectionReason).HasColumnName("rejectionReason").HasMaxLength(1000);
        b.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(2000);

        b.HasOne(x => x.PurchaseOrder).WithMany().HasForeignKey(x => x.PurchaseOrderId)
            .HasConstraintName("FK_PurchaseOrderNegotiation_PurchaseOrder_PurchaseOrderId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_PurchaseOrderNegotiation_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.NegotiationStatus).HasDatabaseName("IX_PurchaseOrderNegotiation_negotiationStatus");
        // Composite scope index — the always-on tenant + company business-data filter scans this path.
        b.HasIndex("TenantId", "TenantEntityId").HasDatabaseName("IX_PurchaseOrderNegotiation_tenant_company");
        // One OPEN (Submitted) negotiation per PO. Filtered on isDeleted=0 so a resolved/soft-deleted negotiation
        // never blocks raising a fresh one; matches the filtered-unique pattern elsewhere in this codebase.
        b.HasIndex(x => x.PurchaseOrderId)
            .HasDatabaseName("UX_PurchaseOrderNegotiation_po_open").IsUnique()
            .HasFilter("[negotiationStatus] = 'Submitted' AND [isDeleted] = 0");
    }
}

// R4 (2026-06-24) — PO Negotiation delta line. Child of the negotiation aggregate (AuditableEntity, two-key +
// audit only, no seccode of its own — the negotiation root carries it). CASCADE from the negotiation, RESTRICT
// to the source PO line (cross-aggregate ERP-master row).
public class PurchaseOrderNegotiationLineConfiguration : IEntityTypeConfiguration<PurchaseOrderNegotiationLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderNegotiationLine> b)
    {
        b.ApplyBaseEntityConvention("PurchaseOrderNegotiationLine", "proc", "purchaseOrderNegotiationLine");
        b.Property(x => x.PurchaseOrderNegotiationId).HasColumnName("purchaseOrderNegotiationId");
        b.Property(x => x.PurchaseOrderLineId).HasColumnName("purchaseOrderLineId");
        b.Property(x => x.PositionNo).HasColumnName("positionNo");
        b.Property(x => x.SequenceNo).HasColumnName("sequenceNo");
        b.Property(x => x.ItemCode).HasColumnName("itemCode").HasMaxLength(50).IsRequired();
        b.Property(x => x.OriginalQty).HasColumnName("originalQty").HasColumnType("decimal(18,4)");
        b.Property(x => x.NegotiatedQty).HasColumnName("negotiatedQty").HasColumnType("decimal(18,4)");
        b.Property(x => x.OriginalDeliveryDate).HasColumnName("originalDeliveryDate").HasColumnType("datetime2");
        b.Property(x => x.NegotiatedDeliveryDate).HasColumnName("negotiatedDeliveryDate").HasColumnType("datetime2");

        b.HasOne(x => x.PurchaseOrderNegotiation).WithMany(p => p.Lines).HasForeignKey(x => x.PurchaseOrderNegotiationId)
            .HasConstraintName("FK_PurchaseOrderNegotiationLine_PurchaseOrderNegotiation_PurchaseOrderNegotiationId")
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.PurchaseOrderLine).WithMany().HasForeignKey(x => x.PurchaseOrderLineId)
            .HasConstraintName("FK_PurchaseOrderNegotiationLine_PurchaseOrderLine_PurchaseOrderLineId")
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.PurchaseOrderNegotiationId).HasDatabaseName("IX_PurchaseOrderNegotiationLine_negotiation");
    }
}

public class DeliveryScheduleConfiguration : IEntityTypeConfiguration<DeliverySchedule>
{
    public void Configure(EntityTypeBuilder<DeliverySchedule> b)
    {
        b.ApplyBaseEntityConvention("DeliverySchedule", "proc", "deliverySchedule");
        b.Property(x => x.PurchaseOrderId).HasColumnName("purchaseOrderId");
        b.Property(x => x.ProposedDate).HasColumnName("proposedDate").HasColumnType("datetime2");
        b.Property(x => x.TimeWindow).HasColumnName("timeWindow").HasMaxLength(50);
        b.Property(x => x.VehicleInfo).HasColumnName("vehicleInfo").HasMaxLength(200);
        b.Property(x => x.ScheduleStatus).HasColumnName("scheduleStatus").HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.ApprovedBy).HasColumnName("approvedBy").HasMaxLength(100);
        b.Property(x => x.RejectionReason).HasColumnName("rejectionReason").HasMaxLength(1000);

        b.HasOne(x => x.PurchaseOrder).WithMany().HasForeignKey(x => x.PurchaseOrderId)
            .HasConstraintName("FK_DeliverySchedule_PurchaseOrder_PurchaseOrderId").OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_DeliverySchedule_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);
    }
}

public class AsnConfiguration : IEntityTypeConfiguration<Asn>
{
    public void Configure(EntityTypeBuilder<Asn> b)
    {
        b.ApplyBaseEntityConvention("Asn", "proc", "asn");
        b.Property(x => x.AsnNumber).HasColumnName("asnNumber").HasMaxLength(50).IsRequired();
        b.Property(x => x.PurchaseOrderId).HasColumnName("purchaseOrderId");
        b.Property(x => x.SupplierId).HasColumnName("supplierId");
        b.Property(x => x.ExpectedDeliveryDate).HasColumnName("expectedDeliveryDate").HasColumnType("datetime2");
        b.Property(x => x.TimeWindow).HasColumnName("timeWindow").HasMaxLength(50);
        b.Property(x => x.CarrierName).HasColumnName("carrierName").HasMaxLength(200);
        b.Property(x => x.TrackingNumber).HasColumnName("trackingNumber").HasMaxLength(100);
        b.Property(x => x.VehicleNumber).HasColumnName("vehicleNumber").HasMaxLength(50);
        b.Property(x => x.DriverName).HasColumnName("driverName").HasMaxLength(100);
        b.Property(x => x.DriverPhone).HasColumnName("driverPhone").HasMaxLength(20);
        b.Property(x => x.AsnStatus).HasColumnName("asnStatus").HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(2000);

        // R4 (2026-06-22) — Module 3: draft/submit lifecycle + ERP ack write-back. erpCode populated via
        // /inbound/erp-ack (the ASNNo). PurchaseOrderId is now NULLABLE (multi-PO via the junction below).
        b.Property(x => x.SubmittedAt).HasColumnName("submittedAt").HasColumnType("datetime2");
        b.Property(x => x.SubmittedBy).HasColumnName("submittedBy").HasMaxLength(100);
        b.Property(x => x.ErpSyncId).HasColumnName("erpSyncId").HasMaxLength(100);
        b.Property(x => x.ErpCode).HasColumnName("erpCode").HasMaxLength(50);

        b.HasOne(x => x.PurchaseOrder).WithMany().HasForeignKey(x => x.PurchaseOrderId)
            .HasConstraintName("FK_Asn_PurchaseOrder_PurchaseOrderId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_Asn_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.AsnNumber).HasDatabaseName("UQ_Asn_asnNumber").IsUnique();
        b.HasIndex(x => x.SupplierId).HasDatabaseName("IX_Asn_supplierId");
        // Composite scope index — the always-on tenant + company business-data filter scans this path.
        b.HasIndex("TenantId", "TenantEntityId").HasDatabaseName("IX_Asn_tenant_company");
    }
}

// R4 (2026-06-22) — Module 3 (Q1): ASN↔PO junction. Child of the ASN aggregate (AuditableEntity, two-key +
// audit only, no seccode of its own — the ASN root carries it). CASCADE from ASN, RESTRICT to PO.
public class AsnPurchaseOrderConfiguration : IEntityTypeConfiguration<AsnPurchaseOrder>
{
    public void Configure(EntityTypeBuilder<AsnPurchaseOrder> b)
    {
        b.ApplyBaseEntityConvention("AsnPurchaseOrder", "proc", "asnPurchaseOrder");
        b.Property(x => x.AsnId).HasColumnName("asnId");
        b.Property(x => x.PurchaseOrderId).HasColumnName("purchaseOrderId");

        b.HasOne(x => x.Asn).WithMany(a => a.PurchaseOrders).HasForeignKey(x => x.AsnId)
            .HasConstraintName("FK_AsnPurchaseOrder_Asn_AsnId").OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.PurchaseOrder).WithMany().HasForeignKey(x => x.PurchaseOrderId)
            .HasConstraintName("FK_AsnPurchaseOrder_PurchaseOrder_PurchaseOrderId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.AsnId).HasDatabaseName("IX_AsnPurchaseOrder_asnId");
        b.HasIndex(x => x.PurchaseOrderId).HasDatabaseName("IX_AsnPurchaseOrder_purchaseOrderId");
        b.HasIndex(x => new { x.AsnId, x.PurchaseOrderId })
            .HasDatabaseName("UQ_AsnPurchaseOrder_asn_po").IsUnique()
            .HasFilter("[isDeleted] = 0");
    }
}

public class AsnLineConfiguration : IEntityTypeConfiguration<AsnLine>
{
    public void Configure(EntityTypeBuilder<AsnLine> b)
    {
        b.ApplyBaseEntityConvention("AsnLine", "proc", "asnLine");
        b.Property(x => x.AsnId).HasColumnName("asnId");
        b.Property(x => x.PurchaseOrderLineId).HasColumnName("purchaseOrderLineId");
        b.Property(x => x.ItemId).HasColumnName("itemId").HasColumnType("uniqueidentifier");
        b.Property(x => x.ShippedQty).HasColumnName("shippedQty").HasColumnType("decimal(18,4)");
        b.Property(x => x.BatchNumber).HasColumnName("batchNumber").HasMaxLength(50);
        b.Property(x => x.ExpiryDate).HasColumnName("expiryDate").HasColumnType("datetime2");

        // R4 (2026-06-22) — Addendum A4: snapshot from the source PO line (backfilled in migration 0019).
        b.Property(x => x.PositionNo).HasColumnName("positionNo");
        b.Property(x => x.SequenceNo).HasColumnName("sequenceNo");

        b.HasOne(x => x.Asn).WithMany(a => a.Lines).HasForeignKey(x => x.AsnId)
            .HasConstraintName("FK_AsnLine_Asn_AsnId").OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.PurchaseOrderLine).WithMany().HasForeignKey(x => x.PurchaseOrderLineId)
            .HasConstraintName("FK_AsnLine_PurchaseOrderLine_PurchaseOrderLineId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId)
            .HasConstraintName("FK_AsnLine_Item_ItemId").OnDelete(DeleteBehavior.Restrict);
    }
}

// R4 (2026-06-23) — serial capture for a serialized ASN line. Child of the ASN aggregate (AuditableEntity,
// two-key + audit only, no seccode of its own — the ASN root carries it; reached via AsnLine). CASCADE from
// the line so deleting the line/ASN clears its serials. Serialized XOR lot-controlled enforced on inv.Item.
public class AsnLineSerialConfiguration : IEntityTypeConfiguration<AsnLineSerial>
{
    public void Configure(EntityTypeBuilder<AsnLineSerial> b)
    {
        b.ApplyBaseEntityConvention("AsnLineSerial", "proc", "asnLineSerial");
        b.Property(x => x.AsnLineId).HasColumnName("asnLineId");
        b.Property(x => x.SerialNumber).HasColumnName("serialNumber").HasMaxLength(100).IsRequired();
        b.Property(x => x.ErpCode).HasColumnName("erpCode").HasMaxLength(50);

        b.HasOne(x => x.AsnLine).WithMany(l => l.Serials).HasForeignKey(x => x.AsnLineId)
            .HasConstraintName("FK_AsnLineSerial_AsnLine_AsnLineId").OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.AsnLineId).HasDatabaseName("IX_AsnLineSerial_asnLineId");
        b.HasIndex(x => new { x.AsnLineId, x.SerialNumber })
            .HasDatabaseName("UQ_AsnLineSerial_asnLine_serial").IsUnique()
            .HasFilter("[isDeleted] = 0");
    }
}

// R4 (2026-06-23) — lot capture for a lot-controlled ASN line. Child of the ASN aggregate (AuditableEntity,
// two-key + audit only, no seccode of its own — reached via AsnLine). CASCADE from the line. Serialized XOR
// lot-controlled enforced on inv.Item.
public class AsnLineLotConfiguration : IEntityTypeConfiguration<AsnLineLot>
{
    public void Configure(EntityTypeBuilder<AsnLineLot> b)
    {
        b.ApplyBaseEntityConvention("AsnLineLot", "proc", "asnLineLot");
        b.Property(x => x.AsnLineId).HasColumnName("asnLineId");
        b.Property(x => x.LotNo).HasColumnName("lotNo").HasMaxLength(100).IsRequired();
        b.Property(x => x.Qty).HasColumnName("qty").HasColumnType("decimal(18,4)");
        b.Property(x => x.ExpiryDate).HasColumnName("expiryDate").HasColumnType("date");
        b.Property(x => x.ErpCode).HasColumnName("erpCode").HasMaxLength(50);

        b.HasOne(x => x.AsnLine).WithMany(l => l.Lots).HasForeignKey(x => x.AsnLineId)
            .HasConstraintName("FK_AsnLineLot_AsnLine_AsnLineId").OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.AsnLineId).HasDatabaseName("IX_AsnLineLot_asnLineId");
        b.HasIndex(x => new { x.AsnLineId, x.LotNo })
            .HasDatabaseName("UQ_AsnLineLot_asnLine_lot").IsUnique()
            .HasFilter("[isDeleted] = 0");
    }
}

public class GoodsReceiptConfiguration : IEntityTypeConfiguration<GoodsReceipt>
{
    public void Configure(EntityTypeBuilder<GoodsReceipt> b)
    {
        b.ApplyBaseEntityConvention("GoodsReceipt", "proc", "goodsReceipt");
        b.Property(x => x.GrnNumber).HasColumnName("grnNumber").HasMaxLength(50).IsRequired();
        b.Property(x => x.PurchaseOrderLineId).HasColumnName("purchaseOrderLineId");
        b.Property(x => x.AsnId).HasColumnName("asnId");
        b.Property(x => x.ReceivedQty).HasColumnName("receivedQty").HasColumnType("decimal(18,4)");
        b.Property(x => x.ShortQty).HasColumnName("shortQty").HasColumnType("decimal(18,4)");
        b.Property(x => x.RejectedQty).HasColumnName("rejectedQty").HasColumnType("decimal(18,4)");
        b.Property(x => x.GrnDate).HasColumnName("grnDate").HasColumnType("datetime2");
        b.Property(x => x.ErpSyncId).HasColumnName("erpSyncId").HasMaxLength(100);

        // R4 (2026-06-22) — Module 5 / Increment D (Q5). GRN status state machine + deterministic invoice link
        // + Payment-Summary "Issue Reported" + ERP ack write-back. Status persisted as the enum name (string),
        // NO DB CHECK; default 'GrnNotApproved' (EF-auto-named DEFAULT, not an explicit DF_ constraint).
        b.Property(x => x.GrnStatus).HasColumnName("grnStatus").HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(Domain.Enums.GrnStatus.GrnNotApproved);
        b.Property(x => x.GrnApprovedAt).HasColumnName("grnApprovedAt").HasColumnType("datetime2");
        b.Property(x => x.InvoiceId).HasColumnName("invoiceId").HasColumnType("uniqueidentifier");
        b.Property(x => x.IssueReported).HasColumnName("issueReported").HasMaxLength(500);
        b.Property(x => x.ErpCode).HasColumnName("erpCode").HasMaxLength(50);

        b.HasOne(x => x.PurchaseOrderLine).WithMany().HasForeignKey(x => x.PurchaseOrderLineId)
            .HasConstraintName("FK_GoodsReceipt_PurchaseOrderLine_PurchaseOrderLineId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Asn).WithMany().HasForeignKey(x => x.AsnId)
            .HasConstraintName("FK_GoodsReceipt_Asn_AsnId").OnDelete(DeleteBehavior.Restrict);
        // Deterministic GRN→Invoice link (replaces the brittle Invoice.GrnReference string). Cross-aggregate → RESTRICT.
        b.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId)
            .HasConstraintName("FK_GoodsReceipt_Invoice_InvoiceId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_GoodsReceipt_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.GrnNumber).HasDatabaseName("IX_GoodsReceipt_grnNumber");
        // Auto-post candidate scan — filtered on the live (non-deleted) rows.
        b.HasIndex(x => x.GrnStatus).HasDatabaseName("IX_GoodsReceipt_grnStatus")
            .HasFilter("[isDeleted] = 0");
        b.HasIndex(x => x.InvoiceId).HasDatabaseName("IX_GoodsReceipt_invoiceId");
        // Composite scope index — the always-on tenant + company business-data filter scans this path
        // (the missing sibling index — schema finding 15).
        b.HasIndex("TenantId", "TenantEntityId").HasDatabaseName("IX_GoodsReceipt_tenant_company");
    }
}

public class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.ApplyBaseEntityConvention("Invoice", "proc", "invoice");
        b.Property(x => x.InvoiceNumber).HasColumnName("invoiceNumber").HasMaxLength(50).IsRequired();
        b.Property(x => x.PurchaseOrderId).HasColumnName("purchaseOrderId");
        b.Property(x => x.AsnId).HasColumnName("asnId");
        b.Property(x => x.SupplierId).HasColumnName("supplierId");
        b.Property(x => x.InvoiceDate).HasColumnName("invoiceDate").HasColumnType("datetime2");
        b.Property(x => x.InvoiceAmount).HasColumnName("invoiceAmount").HasColumnType("decimal(18,4)");
        b.Property(x => x.TaxAmount).HasColumnName("taxAmount").HasColumnType("decimal(18,4)");
        b.Property(x => x.NetAmount).HasColumnName("netAmount").HasColumnType("decimal(18,4)");
        b.Property(x => x.CurrencyCode).HasColumnName("currencyCode").HasMaxLength(10);
        b.Property(x => x.MatchingType).HasColumnName("matchingType").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.GrnReference).HasColumnName("grnReference").HasMaxLength(50);
        b.Property(x => x.InvoiceStatus).HasColumnName("invoiceStatus").HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.RejectionReason).HasColumnName("rejectionReason").HasMaxLength(1000);
        b.Property(x => x.EInvoiceIrn).HasColumnName("eInvoiceIrn").HasMaxLength(100);
        b.Property(x => x.EInvoiceAckNo).HasColumnName("eInvoiceAckNo").HasMaxLength(100);
        b.Property(x => x.EWayBillNumber).HasColumnName("eWayBillNumber").HasMaxLength(50);
        b.Property(x => x.SubmittedBy).HasColumnName("submittedBy").HasMaxLength(100);
        b.Property(x => x.ApprovedBy).HasColumnName("approvedBy").HasMaxLength(100);
        b.Property(x => x.ApprovedAt).HasColumnName("approvedAt").HasColumnType("datetime2");
        b.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(2000);

        // R4 (2026-06-22) — Module 4: posting lifecycle + admin pre-post revoke + ERP ack write-back.
        b.Property(x => x.SubmittedAt).HasColumnName("submittedAt").HasColumnType("datetime2");
        b.Property(x => x.RevokedBy).HasColumnName("revokedBy").HasMaxLength(100);
        b.Property(x => x.RevokedAt).HasColumnName("revokedAt").HasColumnType("datetime2");
        b.Property(x => x.RevokeReason).HasColumnName("revokeReason").HasMaxLength(1000);
        // R4 (2026-06-22, migration 0023, review S2): post-initiated (set at enqueue, gates re-enqueue)
        // is split from erpPostedAt (true ERP success only) so a dispatch failure no longer strands the invoice.
        b.Property(x => x.ErpPostInitiatedAt).HasColumnName("erpPostInitiatedAt").HasColumnType("datetime2");
        b.Property(x => x.ErpPostedAt).HasColumnName("erpPostedAt").HasColumnType("datetime2");
        b.Property(x => x.ErpSyncId).HasColumnName("erpSyncId").HasMaxLength(100);
        b.Property(x => x.ErpCode).HasColumnName("erpCode").HasMaxLength(50);

        b.HasOne(x => x.PurchaseOrder).WithMany().HasForeignKey(x => x.PurchaseOrderId)
            .HasConstraintName("FK_Invoice_PurchaseOrder_PurchaseOrderId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Asn).WithMany().HasForeignKey(x => x.AsnId)
            .HasConstraintName("FK_Invoice_Asn_AsnId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_Invoice_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.SupplierId, x.InvoiceNumber })
            .HasDatabaseName("UQ_Invoice_supplier_invoiceNumber").IsUnique();
        b.HasIndex(x => x.InvoiceStatus).HasDatabaseName("IX_Invoice_invoiceStatus");
        // Composite scope index — the always-on tenant + company business-data filter scans this path.
        b.HasIndex("TenantId", "TenantEntityId").HasDatabaseName("IX_Invoice_tenant_company");
        // R4 (2026-06-22) — Module 4 (Q1b): exactly one (non-deleted) invoice per ASN. Filtered so the
        // legacy non-ASN invoices (asnId NULL) and soft-deleted rows are excluded from the uniqueness.
        b.HasIndex(x => x.AsnId).HasDatabaseName("UQ_Invoice_asnId").IsUnique()
            .HasFilter("[asnId] IS NOT NULL AND [isDeleted] = 0");
    }
}

public class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> b)
    {
        b.ApplyBaseEntityConvention("InvoiceLine", "proc", "invoiceLine");
        b.Property(x => x.InvoiceId).HasColumnName("invoiceId");
        b.Property(x => x.PurchaseOrderLineId).HasColumnName("purchaseOrderLineId");
        b.Property(x => x.ItemCode).HasColumnName("itemCode").HasMaxLength(50).IsRequired();
        b.Property(x => x.ItemDescription).HasColumnName("itemDescription").HasMaxLength(500);
        b.Property(x => x.ItemId).HasColumnName("itemId").HasColumnType("uniqueidentifier");
        b.Property(x => x.BilledQty).HasColumnName("billedQty").HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitPrice).HasColumnName("unitPrice").HasColumnType("decimal(18,4)");
        b.Property(x => x.LineAmount).HasColumnName("lineAmount").HasColumnType("decimal(18,4)");
        b.Property(x => x.TaxCode).HasColumnName("taxCode").HasMaxLength(20);
        b.Property(x => x.TaxAmount).HasColumnName("taxAmount").HasColumnType("decimal(18,4)");

        b.HasOne(x => x.Invoice).WithMany(i => i.Lines).HasForeignKey(x => x.InvoiceId)
            .HasConstraintName("FK_InvoiceLine_Invoice_InvoiceId").OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.PurchaseOrderLine).WithMany().HasForeignKey(x => x.PurchaseOrderLineId)
            .HasConstraintName("FK_InvoiceLine_PurchaseOrderLine_PurchaseOrderLineId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId)
            .HasConstraintName("FK_InvoiceLine_Item_ItemId").OnDelete(DeleteBehavior.Restrict);
    }
}

public class CreditDebitNoteConfiguration : IEntityTypeConfiguration<CreditDebitNote>
{
    public void Configure(EntityTypeBuilder<CreditDebitNote> b)
    {
        b.ApplyBaseEntityConvention("CreditDebitNote", "proc", "creditDebitNote");
        b.Property(x => x.NoteNumber).HasColumnName("noteNumber").HasMaxLength(50).IsRequired();
        b.Property(x => x.NoteType).HasColumnName("noteType").HasConversion<string>().HasMaxLength(2);
        b.Property(x => x.InvoiceId).HasColumnName("invoiceId");
        b.Property(x => x.Amount).HasColumnName("amount").HasColumnType("decimal(18,4)");
        b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(1000);
        b.Property(x => x.NoteStatus).HasColumnName("noteStatus").HasConversion<string>().HasMaxLength(30);

        b.ToTable(t => t.HasCheckConstraint("CK_CreditDebitNote_noteType", "[noteType] IN ('CN','DN')"));

        b.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId)
            .HasConstraintName("FK_CreditDebitNote_Invoice_InvoiceId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_CreditDebitNote_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);
    }
}

public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ApplyBaseEntityConvention("Payment", "proc", "payment");
        b.Property(x => x.PaymentReference).HasColumnName("paymentReference").HasMaxLength(50).IsRequired();
        b.Property(x => x.InvoiceId).HasColumnName("invoiceId");
        b.Property(x => x.SupplierId).HasColumnName("supplierId");
        b.Property(x => x.PaymentDate).HasColumnName("paymentDate").HasColumnType("datetime2");
        b.Property(x => x.PaymentAmount).HasColumnName("paymentAmount").HasColumnType("decimal(18,4)");
        b.Property(x => x.PaymentMode).HasColumnName("paymentMode").HasMaxLength(50);
        b.Property(x => x.BankName).HasColumnName("bankName").HasMaxLength(200);
        b.Property(x => x.BankAccountRef).HasColumnName("bankAccountRef").HasMaxLength(100);
        b.Property(x => x.TdsDeducted).HasColumnName("tdsDeducted").HasColumnType("decimal(18,4)");
        b.Property(x => x.TdsSection).HasColumnName("tdsSection").HasMaxLength(20);
        b.Property(x => x.NetPaid).HasColumnName("netPaid").HasColumnType("decimal(18,4)");
        b.Property(x => x.Remarks).HasColumnName("remarks").HasMaxLength(1000);
        b.Property(x => x.RemittancePdfUrl).HasColumnName("remittancePdfUrl").HasMaxLength(500);
        b.Property(x => x.ErpSyncId).HasColumnName("erpSyncId").HasMaxLength(100);

        // R4 (2026-06-22) — Module 5 / Increment D (H1: inbound Payment sync). ERP ack write-back code.
        b.Property(x => x.ErpCode).HasColumnName("erpCode").HasMaxLength(50);

        b.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId)
            .HasConstraintName("FK_Payment_Invoice_InvoiceId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.SeccodeId)
            .HasConstraintName("FK_Payment_Seccode_SeccodeId").OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.SupplierId).HasDatabaseName("IX_Payment_supplierId");
        // R4 (2026-06-22) — Module 5 / Increment D (H1): the inbound writer correlates payments to invoices via
        // invoiceId; name the FK's covering index so it's deterministic (replaces EF's unnamed shadow index).
        b.HasIndex(x => x.InvoiceId).HasDatabaseName("IX_Payment_invoiceId");
        // Composite scope index — the always-on tenant + company business-data filter scans this path.
        b.HasIndex("TenantId", "TenantEntityId").HasDatabaseName("IX_Payment_tenant_company");
        // R4 (2026-06-24) — DB-level dedup guard for inbound Payment sync. Concurrent inbound pushes race past the
        // app-level read-modify-write dedup (UpsertPaymentsCommand.cs:79-82); this filtered unique index enforces the
        // app's effective uniqueness scope (tenant + invoice + reference). Filtered on isDeleted=0 so soft-deleted rows
        // never block re-insert of the same logical payment; paymentReference IS NOT NULL future-proofs the guard.
        b.HasIndex("TenantId", "TenantEntityId", nameof(Payment.InvoiceId), nameof(Payment.PaymentReference))
            .HasDatabaseName("UX_Payment_tenant_invoice_paymentReference").IsUnique()
            .HasFilter("[isDeleted] = 0 AND [paymentReference] IS NOT NULL");
    }
}
