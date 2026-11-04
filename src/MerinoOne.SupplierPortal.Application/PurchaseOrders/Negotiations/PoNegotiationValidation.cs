using System.Linq.Expressions;
using FluentValidation;
using MerinoOne.SupplierPortal.Contracts.PurchaseOrders;

namespace MerinoOne.SupplierPortal.Application.PurchaseOrders.Negotiations;

/// <summary>
/// R18 (2026-11-03) — the rules on ONE proposed line's own fields, shared by every command that carries a proposal (the
/// supplier's create and either side's counter). The PO-dependent rules (delivery floor, quantity scale) live in
/// <see cref="PoNegotiationLineRules"/>.
/// </summary>
internal sealed class PoNegotiationLineInputValidator : AbstractValidator<PoNegotiationLineInput>
{
    public PoNegotiationLineInputValidator()
    {
        RuleFor(l => l.PurchaseOrderLineId).NotEmpty();
        RuleFor(l => l.NegotiatedQty).GreaterThan(0).WithMessage("Negotiated quantity must be greater than zero.");
        RuleFor(l => l.NegotiatedPrice).GreaterThanOrEqualTo(0).WithMessage("Negotiated price cannot be negative.");
    }
}

internal static class PoNegotiationValidation
{
    private const int NotesMaxLength = 1000;
    private const int ReasonMaxLength = 1000;

    /// <summary>
    /// The rules a proposal's own fields must satisfy before the handler's PO-dependent rules run: notes at most 1000
    /// characters, at least one line, and every line valid (<see cref="PoNegotiationLineInputValidator"/>).
    /// </summary>
    public static void AddProposalRules<T>(
        this AbstractValidator<T> validator,
        Expression<Func<T, string?>> notes,
        Expression<Func<T, IEnumerable<PoNegotiationLineInput>>> lines)
    {
        validator.RuleFor(notes).MaximumLength(NotesMaxLength);
        validator.RuleFor(lines).NotEmpty().WithMessage("At least one line is required.");
        validator.RuleForEach(lines).SetValidator(new PoNegotiationLineInputValidator());
    }

    /// <summary>The reason a party gives for turning a round down (buyer reject, supplier decline): required, at most 1000.</summary>
    public static IRuleBuilderOptions<T, string> RejectionReason<T>(this IRuleBuilder<T, string> rule)
        => rule.NotEmpty().MaximumLength(ReasonMaxLength).WithMessage("A rejection reason is required.");
}
