using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Exceptions;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Contracts.Admin;
using MerinoOne.SupplierPortal.Domain.Entities.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MerinoOne.SupplierPortal.Application.Admin.EmailTemplates;

/// <summary>
/// Updates the editable fields (Subject, HtmlBody, IsActive) of an email template. Looks up
/// the row by <c>TemplateKey</c> (route parameter), applies audit fields, and evicts the
/// renderer's cache entry so the next send picks up the new content immediately.
/// </summary>
public record UpdateEmailTemplateCommand(string Key, UpdateEmailTemplateRequest Body) : IRequest<Unit>;

public class UpdateEmailTemplateCommandValidator : AbstractValidator<UpdateEmailTemplateCommand>
{
    public UpdateEmailTemplateCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Body.Subject)
            .NotEmpty().WithMessage("Subject is required.")
            .MaximumLength(300);
        RuleFor(x => x.Body.HtmlBody)
            .NotEmpty().WithMessage("HtmlBody is required.")
            // Radzen HtmlEditor output can grow quickly when embedded styles + images creep in;
            // 100K covers every realistic template without admitting arbitrary uploads.
            .MaximumLength(100_000);
    }
}

public class UpdateEmailTemplateCommandHandler : IRequestHandler<UpdateEmailTemplateCommand, Unit>
{
    // Keep in sync with EmailTemplateRenderer.CacheKeyPrefix — Application layer can't reference
    // the Infrastructure project, so the literal is duplicated here with a load-bearing comment.
    private const string CacheKeyPrefix = "emailtpl:";

    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMemoryCache _cache;

    public UpdateEmailTemplateCommandHandler(IAppDbContext db, ICurrentUser user, IMemoryCache cache)
    {
        _db = db;
        _user = user;
        _cache = cache;
    }

    public async Task<Unit> Handle(UpdateEmailTemplateCommand request, CancellationToken ct)
    {
        var row = await _db.EmailTemplates
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TemplateKey == request.Key && !t.IsDeleted, ct);

        if (row is null)
            throw new NotFoundException(nameof(EmailTemplate), request.Key);

        row.Subject = request.Body.Subject;
        row.HtmlBody = request.Body.HtmlBody;
        row.IsActive = request.Body.IsActive;
        row.UpdatedBy = string.IsNullOrEmpty(_user?.UserCode) ? "system" : _user.UserCode;
        row.UpdatedOn = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // Eject the renderer's cache entry — the next send picks up the new subject/body
        // without waiting for the 60s TTL to expire.
        _cache.Remove(CacheKeyPrefix + request.Key);

        return Unit.Value;
    }
}
