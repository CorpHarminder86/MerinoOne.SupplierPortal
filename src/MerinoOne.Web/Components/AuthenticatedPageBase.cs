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
    }
}
