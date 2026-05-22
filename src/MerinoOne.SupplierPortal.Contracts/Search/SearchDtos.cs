namespace MerinoOne.SupplierPortal.Contracts.Search;

/// <summary>
/// Uniform shape returned by the global search endpoint across modules.
/// <para><c>Module</c> is one of:
/// Supplier | PurchaseOrder | Invoice | Asn | GoodsReceipt | Payment | CommunicationMessage.</para>
/// </summary>
public record SearchResultDto(
    string Module,
    Guid Id,
    string Code,
    string Title,
    string Subtitle,
    string Status,
    DateTime When);
