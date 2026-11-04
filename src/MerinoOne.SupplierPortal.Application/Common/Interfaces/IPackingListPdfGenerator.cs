using MerinoOne.SupplierPortal.Contracts.Shipments;

namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// R18 (2026-11-03) — renders an ASN's Packing List read model (<see cref="PackingListDto"/> — every value already
/// resolved and formatted by <c>GetPackingListQuery</c>, so the renderer does no lookups) to an A4 PDF byte stream.
/// Implemented in Infrastructure (QuestPDF, Community license set at startup); consumed by
/// <c>GET api/asns/{id}/packing-list</c>.
/// </summary>
public interface IPackingListPdfGenerator
{
    byte[] Generate(PackingListDto packingList);
}
