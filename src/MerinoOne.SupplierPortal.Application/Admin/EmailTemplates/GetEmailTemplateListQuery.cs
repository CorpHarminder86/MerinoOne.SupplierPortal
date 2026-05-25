using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Admin;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.Admin.EmailTemplates;

/// <summary>
/// Returns every email template (including inactive rows) ordered by TemplateKey for the
/// admin list page. Tombstoned rows are excluded explicitly so the admin doesn't see
/// soft-deleted entries.
/// </summary>
public record GetEmailTemplateListQuery() : IRequest<List<EmailTemplateDto>>;

public class GetEmailTemplateListQueryHandler : IRequestHandler<GetEmailTemplateListQuery, List<EmailTemplateDto>>
{
    private readonly IAppDbContext _db;
    public GetEmailTemplateListQueryHandler(IAppDbContext db) => _db = db;

    public async Task<List<EmailTemplateDto>> Handle(GetEmailTemplateListQuery request, CancellationToken ct)
    {
        return await _db.EmailTemplates
            .IgnoreQueryFilters()
            .Where(t => !t.IsDeleted)
            .OrderBy(t => t.TemplateKey)
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
            .ToListAsync(ct);
    }
}
