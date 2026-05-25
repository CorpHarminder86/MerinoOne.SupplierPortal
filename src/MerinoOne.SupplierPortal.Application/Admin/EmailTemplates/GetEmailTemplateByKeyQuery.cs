using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Admin.EmailTemplates;

/// <summary>
/// Loads a single template by <c>TemplateKey</c> for the admin edit page. Throws
/// <see cref="NotFoundException"/> when no row exists (or the row is tombstoned).
/// </summary>
public record GetEmailTemplateByKeyQuery(string Key) : IRequest<EmailTemplateDto>;

public class GetEmailTemplateByKeyQueryHandler : IRequestHandler<GetEmailTemplateByKeyQuery, EmailTemplateDto>
{
    private readonly IAppDbContext _db;
    public GetEmailTemplateByKeyQueryHandler(IAppDbContext db) => _db = db;

    public async Task<EmailTemplateDto> Handle(GetEmailTemplateByKeyQuery request, CancellationToken ct)
    {
        var row = await _db.EmailTemplates
            .IgnoreQueryFilters()
            .Where(t => t.TemplateKey == request.Key && !t.IsDeleted)
            .Select(t => new EmailTemplateDto(
                t.Id,
                t.Seq,
                t.TemplateKey,
                t.Subject,
                t.HtmlBody,
                t.IsActive,
                t.Notes,
                t.CreatedOn,
                t.UpdatedOn))
            .FirstOrDefaultAsync(ct);

        if (row is null)
            throw new NotFoundException(nameof(EmailTemplate), request.Key);

        return row;
    }
}
