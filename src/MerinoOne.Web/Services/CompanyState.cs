namespace MerinoOne.Web.Services;

using MerinoOne.SupplierPortal.Contracts.Companies;

/// <summary>
/// Scoped, circuit-lived holder for the user's active-company selection and the list of companies
/// they can access. The selected <see cref="ActiveCompanyId"/> is emitted on every API call as the
/// <c>X-Active-Company</c> header so the server's always-on company filter scopes business data.
///
/// Header injection note: the backend reads <c>X-Active-Company</c> per request. A classic
/// <see cref="System.Net.Http.DelegatingHandler"/> registered through IHttpClientFactory cannot see
/// this value because the handler chain is NOT part of the Blazor circuit scope — it has its own
/// (effectively singleton) lifetime and would resolve a different CompanyState instance than the one
/// the UI mutates. The codebase already proves this constraint: <see cref="ApiClient"/> attaches the
/// bearer token by setting <c>DefaultRequestHeaders.Authorization</c> from the scoped
/// <see cref="TokenAccessor"/> on each call rather than via a handler. We mirror that exact pattern for
/// the company header — <see cref="ApiClient.EnsureAuth"/> reads <see cref="ActiveCompanyId"/> from this
/// scoped service and stamps the header. This keeps the selection circuit-scoped and correct.
///
/// Persistence: the selection survives navigation (scoped lifetime) and page refresh (re-hydrated from
/// ProtectedLocalStorage by MainLayout on circuit start), so a reload keeps the same active company.
/// </summary>
public class CompanyState
{
    private Guid? _activeCompanyId;

    /// <summary>Companies the current user can access (drives the header selector + dropdowns).</summary>
    public IReadOnlyList<CompanyDto> AccessibleCompanies { get; private set; } = Array.Empty<CompanyDto>();

    /// <summary>The company emitted as X-Active-Company on every API call. Null until a selection lands.</summary>
    public Guid? ActiveCompanyId => _activeCompanyId;

    /// <summary>True once the accessible-company list has been loaded (even if it is empty).</summary>
    public bool Loaded { get; private set; }

    /// <summary>True when the user can switch between more than one company (show the selector).</summary>
    public bool HasMultiple => AccessibleCompanies.Count > 1;

    public CompanyDto? Active => _activeCompanyId is { } id
        ? AccessibleCompanies.FirstOrDefault(c => c.Id == id)
        : null;

    public event Action? Changed;

    /// <summary>
    /// Seed the accessible set + choose an active company. If <paramref name="preferredId"/> (e.g. a
    /// persisted selection) is still accessible it wins; otherwise the first company is selected.
    /// </summary>
    public void SetCompanies(IReadOnlyList<CompanyDto> companies, Guid? preferredId = null)
    {
        AccessibleCompanies = companies ?? Array.Empty<CompanyDto>();
        Loaded = true;

        if (preferredId.HasValue && AccessibleCompanies.Any(c => c.Id == preferredId.Value))
            _activeCompanyId = preferredId;
        else if (_activeCompanyId.HasValue && AccessibleCompanies.Any(c => c.Id == _activeCompanyId.Value))
        {
            // keep the existing valid selection
        }
        else
            _activeCompanyId = AccessibleCompanies.Count > 0 ? AccessibleCompanies[0].Id : null;

        Changed?.Invoke();
    }

    /// <summary>Switch the active company. No-op if the id is not in the accessible set.</summary>
    public void SetActive(Guid companyId)
    {
        if (_activeCompanyId == companyId) return;
        if (!AccessibleCompanies.Any(c => c.Id == companyId)) return;
        _activeCompanyId = companyId;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _activeCompanyId = null;
        AccessibleCompanies = Array.Empty<CompanyDto>();
        Loaded = false;
        Changed?.Invoke();
    }
}
