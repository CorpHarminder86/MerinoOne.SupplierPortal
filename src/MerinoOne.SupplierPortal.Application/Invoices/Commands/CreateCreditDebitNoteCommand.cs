using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Domain.Entities.Proc;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Invoices.Commands;

public record CreateCreditDebitNoteCommand(CreateCreditDebitNoteRequest Body) : IRequest<CreditDebitNoteDetailDto>;

public class CreateCreditDebitNoteCommandValidator : AbstractValidator<CreateCreditDebitNoteCommand>
{
    public CreateCreditDebitNoteCommandValidator()
    {
        RuleFor(x => x.Body.InvoiceId).NotEmpty();
        RuleFor(x => x.Body.NoteNumber).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.NoteType).NotEmpty()
            .Must(v => v == nameof(NoteType.CN) || v == nameof(NoteType.DN))
            .WithMessage("NoteType must be 'CN' or 'DN'.");
        RuleFor(x => x.Body.Amount).GreaterThan(0);
    }
}

public class CreateCreditDebitNoteCommandHandler : IRequestHandler<CreateCreditDebitNoteCommand, CreditDebitNoteDetailDto>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public CreateCreditDebitNoteCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db; _user = user;
    }

    public async Task<CreditDebitNoteDetailDto> Handle(CreateCreditDebitNoteCommand request, CancellationToken ct)
    {
        var body = request.Body;

        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.Id == body.InvoiceId, ct)
                      ?? throw new NotFoundException("Invoice", body.InvoiceId);

        var supplier = await _db.Suppliers.FirstOrDefaultAsync(s => s.Id == invoice.SupplierId, ct)
                       ?? throw new NotFoundException("Supplier", invoice.SupplierId);

        var dup = await _db.CreditDebitNotes.AnyAsync(n => n.NoteNumber == body.NoteNumber, ct);
        if (dup)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["noteNumber"] = new[] { $"Note number '{body.NoteNumber}' already exists." }
            });
        }

        var noteType = body.NoteType == nameof(NoteType.CN) ? NoteType.CN : NoteType.DN;

        var note = new CreditDebitNote
        {
            Id = Guid.NewGuid(),
            NoteNumber = body.NoteNumber,
            NoteType = noteType,
            InvoiceId = invoice.Id,
            Amount = body.Amount,
            Reason = body.Reason,
            NoteStatus = NoteStatus.Submitted,
            SeccodeId = supplier.SeccodeId,
            CreatedBy = _user.UserCode,
            CreatedOn = DateTime.UtcNow,
        };

        _db.CreditDebitNotes.Add(note);
        await _db.SaveChangesAsync(ct);

        return new CreditDebitNoteDetailDto(
            note.Id,
            note.Seq,
            note.NoteNumber,
            note.NoteType.ToString(),
            note.InvoiceId,
            invoice.InvoiceNumber,
            invoice.SupplierId,
            supplier.LegalName,
            note.Amount,
            note.Reason,
            note.NoteStatus.ToString(),
            note.CreatedOn);
    }
}
