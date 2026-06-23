using System.Text.Json;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Infor;

/// <summary>
/// Shared builder for the outbound Invoice→ERP request body. BOTH <see cref="LiveInforIntegrationService"/> (for the
/// HTTP POST body) and <see cref="MockInforIntegrationService"/> (so dev gets the identical canonical "what we sent"
/// payload) call this so the JSON persisted to <c>InforSyncLog.PayloadJson</c> is byte-for-byte the same in Mock and
/// Live. The shape, field map, and serializer options mirror exactly what the Live invoice post builds — keep them in
/// lock-step.
///
/// TODO (per-tenant Infor LN spec): the LN self-billing / invoice field map is a STARTER — confirm with the Infor LN
/// team (incl. lines if required) before enabling Mode=Live.
/// </summary>
internal static class InvoiceOutboundPayloadBuilder
{
    /// <summary>Serializer options shared with the Live POST body: <c>WhenWritingNull</c> drops empty fields.</summary>
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Builds and serializes the outbound invoice payload — or <c>null</c> if the invoice does not exist. The returned
    /// object is also what the Live service POSTs, so the two never drift.
    /// </summary>
    internal static async Task<string?> BuildJsonAsync(IAppDbContext db, Guid invoiceId, CancellationToken ct = default)
    {
        var payload = await BuildPayloadAsync(db, invoiceId, ct);
        return payload is null ? null : JsonSerializer.Serialize(payload, JsonOpts);
    }

    /// <summary>
    /// Builds the anonymous invoice payload object, or <c>null</c> when the invoice is not found. Live serializes this
    /// for the POST body; the JSON is also persisted for the SyncLog viewer.
    /// </summary>
    internal static async Task<object?> BuildPayloadAsync(IAppDbContext db, Guid invoiceId, CancellationToken ct = default)
    {
        // IgnoreQueryFilters: this runs in the background OutboxDispatcher scope, which has NO ambient tenant/seccode
        // (the dispatcher reads everything with IgnoreQueryFilters), so the tenant/company global filters would
        // otherwise return null. We re-apply the soft-delete guard explicitly since it's dropped too.
        var invoice = await db.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted, ct);
        if (invoice is null) return null;

        // TODO: confirm the real LN self-billing / invoice field map (incl. lines if required).
        return new
        {
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate.ToString("o"),
            Currency = invoice.CurrencyCode,
            InvoiceAmount = invoice.InvoiceAmount,
            TaxAmount = invoice.TaxAmount,
            NetAmount = invoice.NetAmount,
            EInvoiceIrn = invoice.EInvoiceIrn,
        };
    }
}
