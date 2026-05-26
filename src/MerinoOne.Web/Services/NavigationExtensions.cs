using Microsoft.AspNetCore.Components;

namespace MerinoOne.Web.Services;

/// <summary>
/// Sub-path-safe navigation. Blazor's <see cref="NavigationManager.NavigateTo(string, bool)"/>
/// treats a leading slash as absolute host root, which strips the configured
/// <c>app.UsePathBase("/supplier-portal-dev")</c> prefix when deployed behind a reverse proxy.
///
/// Every page should call <c>Nav.NavigateToRoute("/login")</c> instead of
/// <c>Nav.NavigateTo("/login")</c> so routes resolve relative to the deployed BaseUri.
/// </summary>
public static class NavigationExtensions
{
    public static void NavigateToRoute(this NavigationManager nav, string path, bool forceLoad = false)
    {
        // Pass through absolute URIs unchanged.
        if (path.StartsWith("http://") || path.StartsWith("https://"))
        {
            nav.NavigateTo(path, forceLoad);
            return;
        }

        // BaseUri already includes the PathBase prefix (set by UsePathBase or the <base href>
        // emitted by App.razor). Append the route segment without the leading slash so
        // Uri composition keeps the sub-path intact.
        var rel = path.TrimStart('/');
        nav.NavigateTo(nav.BaseUri + rel, forceLoad);
    }
}
