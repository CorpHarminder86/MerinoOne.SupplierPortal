using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.SystemSettings.EmailConfig;
using MerinoOne.SupplierPortal.Application.SystemSettings.Registry;
using MerinoOne.SupplierPortal.Contracts.SystemSettings;
using Microsoft.EntityFrameworkCore;

namespace MerinoOne.SupplierPortal.Application.SystemSettings.Queries;

/// <summary>
/// Fetches every active setting row for a category, synthesises any rows missing from the
/// DB but declared by the category's seed (so the UI always has a complete form), and masks
/// sensitive values (EmailConfig.Password) before returning.
/// </summary>
public record GetSystemSettingsQuery(string Category) : IRequest<List<SystemSettingDto>>;

public class GetSystemSettingsQueryHandler : IRequestHandler<GetSystemSettingsQuery, List<SystemSettingDto>>
{
    private readonly IAppDbContext _db;
    private readonly SettingsSeedRegistry _registry;

    public GetSystemSettingsQueryHandler(IAppDbContext db, SettingsSeedRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    public async Task<List<SystemSettingDto>> Handle(GetSystemSettingsQuery request, CancellationToken ct)
    {
        var category = request.Category ?? string.Empty;

        var rows = await _db.SystemSettings
            .Where(s => s.Category == category && s.IsActive)
            .OrderBy(s => s.SettingKey)
            .Select(s => new
            {
                s.Id,
                s.Seq,
                s.Category,
                s.SettingKey,
                s.SettingValue,
                s.Description,
                s.IsActive,
                s.CreatedOn,
                s.UpdatedOn
            })
            .ToListAsync(ct);

        ISettingsCategorySeed? seed = null;
        _registry.TryGet(category, out seed);

        var byKey = rows.ToDictionary(r => r.SettingKey, StringComparer.Ordinal);
        var dtos = new List<SystemSettingDto>();

        // Synthesise rows for any seed-declared keys missing from the DB (defensive — the
        // migration seeds them, but a fresh test slice or a partial restore could leave gaps).
        if (seed != null)
        {
            foreach (var key in seed.Defaults.Keys)
            {
                if (byKey.ContainsKey(key)) continue;
                seed.Descriptions.TryGetValue(key, out var desc);
                dtos.Add(BuildDto(
                    id: Guid.Empty,
                    seq: 0,
                    category: category,
                    key: key,
                    value: seed.Defaults[key],
                    description: desc,
                    isActive: true,
                    createdOn: DateTime.UtcNow,
                    updatedOn: null,
                    seed: seed));
            }
        }

        foreach (var r in rows)
        {
            dtos.Add(BuildDto(
                id: r.Id,
                seq: r.Seq,
                category: r.Category,
                key: r.SettingKey,
                value: r.SettingValue,
                description: r.Description,
                isActive: r.IsActive,
                createdOn: r.CreatedOn,
                updatedOn: r.UpdatedOn,
                seed: seed));
        }

        return dtos
            .OrderBy(d => d.SettingKey, StringComparer.Ordinal)
            .ToList();
    }

    private static SystemSettingDto BuildDto(
        Guid id, int seq, string category, string key, string value, string? description,
        bool isActive, DateTime createdOn, DateTime? updatedOn, ISettingsCategorySeed? seed)
    {
        // Mask the SMTP password before it leaves the API. UI sends back the sentinel if
        // the operator did not retype the value; the Save handler treats that as "no change".
        var outValue = value;
        if (string.Equals(category, EmailConfigKeys.Category, StringComparison.Ordinal)
            && string.Equals(key, EmailConfigKeys.Password, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(value))
        {
            outValue = EmailConfigKeys.PasswordMask;
        }

        string? defaultString = null;
        int? defaultInt = null;
        if (seed != null && seed.Defaults.TryGetValue(key, out var defStr))
        {
            defaultString = defStr;
            if (int.TryParse(defStr, out var parsed)) defaultInt = parsed;
        }

        return new SystemSettingDto(
            Id: id,
            Seq: seq,
            Category: category,
            SettingKey: key,
            SettingValue: outValue,
            Description: description,
            IsActive: isActive,
            DefaultValueString: defaultString,
            DefaultValueInt: defaultInt,
            CreatedOn: createdOn,
            UpdatedOn: updatedOn);
    }
}
