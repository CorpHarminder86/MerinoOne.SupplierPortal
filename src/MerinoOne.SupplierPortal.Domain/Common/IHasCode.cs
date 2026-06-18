namespace MerinoOne.SupplierPortal.Domain.Common;

/// <summary>Marker for entities with a natural <c>Code</c> key — lets generic helpers read it.</summary>
public interface IHasCode
{
    string Code { get; }
}
