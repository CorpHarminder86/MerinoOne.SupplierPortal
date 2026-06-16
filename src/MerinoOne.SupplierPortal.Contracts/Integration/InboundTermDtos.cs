using System.Text.Json.Serialization;

namespace MerinoOne.SupplierPortal.Contracts.Integration;

/// <summary>
/// One inbound Payment Term record pushed by Infor LN. <see cref="Code"/> is the natural key
/// (upsert key together with the resolved source company). <see cref="NetDays"/> is the credit
/// period in days (0..365).
/// </summary>
public record PaymentTermRecord(
    string Code,
    string Description,
    int NetDays,
    bool IsActive = true);

/// <summary>One inbound Delivery Term record pushed by Infor LN.</summary>
public record DeliveryTermRecord(
    string Code,
    string Description,
    bool IsActive = true);

/// <summary>
/// Inbound Payment Term push body. <see cref="CompanyCode"/> is the Infor LN logistic company code
/// (e.g. "3000"); it is resolved to a TenantEntity within the key's tenant and then normalized to its
/// share-group source company before the upsert. Unknown company ⇒ 400; resolves to a different source
/// than the key's bound company ⇒ 403.
/// </summary>
public record PushPaymentTermsRequest(
    string CompanyCode,
    IReadOnlyList<PaymentTermRecord> Terms);

/// <summary>Inbound Delivery Term push body. See <see cref="PushPaymentTermsRequest"/> for the company semantics.</summary>
public record PushDeliveryTermsRequest(
    string CompanyCode,
    IReadOnlyList<DeliveryTermRecord> Terms);

/// <summary>Per-row outcome of an inbound upsert. Serialized as its string name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RowOutcome { Inserted, Updated, Skipped, Failed }

/// <summary>The result of one record in the inbound batch. <see cref="Error"/> is set only on Failed.</summary>
public record RowResult(string Code, RowOutcome Outcome, string? Error);

/// <summary>
/// Aggregate result of an inbound upsert batch. Returned with HTTP 200 even when some rows failed —
/// partial failures are flagged per-row (and a linked IntegrationError is recorded for operator retry).
/// </summary>
public record UpsertResultDto(
    string CompanyCode,
    int Received,
    int Inserted,
    int Updated,
    int Skipped,
    int Failed,
    IReadOnlyList<RowResult> Rows);
