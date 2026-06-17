using MerinoOne.Web.Services;
using Microsoft.AspNetCore.Components;

namespace MerinoOne.Web.Components;

public abstract class AuthenticatedPageBase : ComponentBase, IDisposable
{
    [Inject] protected TokenAccessor AuthState { get; set; } = default!;
    [Inject] protected PageRefreshService PageRefresh { get; set; } = default!;
    [Inject] protected ApiErrorNotifier ErrorNotifier { get; set; } = default!;
    [Inject] protected NavigationManager Navigation { get; set; } = default!;

    protected bool LoadFailed { get; private set; }

    private bool _firstRenderDone;
    private bool _loadedOnce;

    protected override void OnInitialized()
    {
        PageRefresh.RefreshRequested += HandleGlobalRefresh;
        // On a hard page-load the circuit re-hydrates the token via /api/auth/me AFTER first render,
        // so permission-gated content (e.g. an "Integration access required" guard) would otherwise
        // render denied and never re-check. Re-render when the token/permissions change so the gate
        // resolves correctly once hydration completes (or after a Refresh-permissions action).
        AuthState.Changed += OnAuthStateChanged;
    }

    private void OnAuthStateChanged()
    {
        // Token (re)hydrated. On a hard page-load the first LoadDataAsync can run before /api/auth/me
        // repopulates permissions — a permission-gated LoadDataAsync then early-returns empty. Once the
        // token changes (hydration complete / permissions refreshed), re-fetch so the page fills in.
        if (AuthState.IsAuthenticated && _firstRenderDone)
            _ = InvokeAsync(ReloadAsync);
        else
            _ = InvokeAsync(StateHasChanged);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _firstRenderDone = true;
            if (AuthState.IsAuthenticated && !_loadedOnce)
                await ReloadAsync();
        }
    }

    protected virtual Task LoadDataAsync() => Task.CompletedTask;

    protected virtual Task OnLoadFailedAsync(Exception ex) => Task.CompletedTask;

    protected async Task ReloadAsync()
    {
        LoadFailed = false;
        try
        {
            await LoadDataAsync();
            _loadedOnce = true;
        }
        catch (Exception ex)
        {
            LoadFailed = true;
            try { ErrorNotifier.Show(ex); } catch { }
            try { await OnLoadFailedAsync(ex); } catch { }
        }
        finally { StateHasChanged(); }
    }

    private Task HandleGlobalRefresh() =>
        AuthState.IsAuthenticated && _firstRenderDone
            ? InvokeAsync(ReloadAsync)
            : Task.CompletedTask;

    public virtual void Dispose()
    {
        PageRefresh.RefreshRequested -= HandleGlobalRefresh;
        AuthState.Changed -= OnAuthStateChanged;
    }
}
