using MerinoOne.SupplierPortal.Application.Integration.Ln;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Ln.InputDocuments;

/// <summary>R9 — DI-composed <c>portalEntity → builder</c> registry. Duplicate registration is a startup error.</summary>
public sealed class LnInputDocumentBuilderRegistry : ILnInputDocumentBuilderRegistry
{
    private readonly Dictionary<string, ILnInputDocumentBuilder> _byEntity;

    public LnInputDocumentBuilderRegistry(IEnumerable<ILnInputDocumentBuilder> builders)
    {
        _byEntity = new Dictionary<string, ILnInputDocumentBuilder>(StringComparer.Ordinal);
        foreach (var b in builders)
        {
            if (!_byEntity.TryAdd(b.PortalEntity, b))
                throw new InvalidOperationException($"Duplicate LN input-document builder for portalEntity '{b.PortalEntity}'.");
        }
    }

    public ILnInputDocumentBuilder? TryGet(string portalEntity)
        => _byEntity.TryGetValue(portalEntity, out var b) ? b : null;

    public IReadOnlyCollection<ILnInputDocumentBuilder> All => _byEntity.Values;
}
