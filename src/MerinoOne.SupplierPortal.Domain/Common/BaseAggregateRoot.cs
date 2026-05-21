using System.ComponentModel.DataAnnotations.Schema;

namespace MerinoOne.SupplierPortal.Domain.Common;

public abstract class BaseAggregateRoot : AuditableEntity, IHasRowVersion, ISeccode, ITenantScoped
{
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
    public Guid SeccodeId { get; set; }
    public Entities.Admin.Seccode? Owner { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? TenantEntityId { get; set; }

    [NotMapped]
    private readonly List<IDomainEvent> _domainEvents = new();
    [NotMapped]
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
    public void RaiseEvent(IDomainEvent evt) => _domainEvents.Add(evt);
    public void ClearEvents() => _domainEvents.Clear();
}

public interface IDomainEvent { }
