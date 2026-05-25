namespace MerinoOne.Web.Services;

public sealed class PageRefreshService
{
    public event Func<Task>? RefreshRequested;

    public async Task RequestRefreshAsync()
    {
        if (RefreshRequested is null) return;
        var handlers = RefreshRequested.GetInvocationList().Cast<Func<Task>>();
        foreach (var h in handlers)
        {
            try { await h(); } catch { /* one page failing shouldn't block others */ }
        }
    }
}
