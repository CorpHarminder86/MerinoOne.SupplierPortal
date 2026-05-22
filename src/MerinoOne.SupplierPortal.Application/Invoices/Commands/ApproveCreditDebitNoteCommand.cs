using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Invoices;
using MerinoOne.SupplierPortal.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using NotFoundException = MerinoOne.SupplierPortal.Application.Common.Exceptions.NotFoundException;
using ValidationException = MerinoOne.SupplierPortal.Application.Common.Exceptions.ValidationException;

namespace MerinoOne.SupplierPortal.Application.Invoices.Commands;

public record ApproveCreditDebitNoteCommand(Guid NoteId, ApproveCreditDebitNoteRequest Body) : IRequest<Unit>;

public class ApproveCreditDebitNoteCommandHandler : IRequestHandler<ApproveCreditDebitNoteCommand, Unit>
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;

    public ApproveCreditDebitNoteCommandHandler(IAppDbContext db, ICurrentUser user)
    {
        _db = db; _user = user;
    }

    public async Task<Unit> Handle(ApproveCreditDebitNoteCommand request, CancellationToken ct)
    {
        var note = await _db.CreditDebitNotes.FirstOrDefaultAsync(n => n.Id == request.NoteId, ct)
                   ?? throw new NotFoundException("CreditDebitNote", request.NoteId);

        if (note.NoteStatus == NoteStatus.Approved)
            return Unit.Value;

        if (note.NoteStatus != NoteStatus.Submitted && note.NoteStatus != NoteStatus.Draft)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["noteStatus"] = new[] { $"Credit/debit note cannot be approved from current state '{note.NoteStatus}'." }
            });
        }

        note.NoteStatus = NoteStatus.Approved;
        note.UpdatedBy = _user.UserCode;
        note.UpdatedOn = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Unit.Value;
    }
}
