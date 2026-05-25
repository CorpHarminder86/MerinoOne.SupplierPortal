using MerinoOne.Web.Components.Shared;
using Radzen;

namespace MerinoOne.Web.Services;

/// <summary>
/// House-style confirm dialogs. Every destructive action (delete / unmap / deactivate / revoke)
/// must call <see cref="ConfirmDeleteAsync"/> before issuing the API request. Renders the custom
/// <see cref="ConfirmDialog"/> component inside Radzen's dialog frame so styling matches merino.css.
/// </summary>
public static class DialogServiceExtensions
{
    public static async Task<bool> ConfirmDeleteAsync(
        this DialogService dialog,
        string itemName,
        string category = "DELETE",
        string actionLabel = "Delete",
        string? subtitle = null,
        string icon = "delete",
        string intent = "danger")
    {
        var result = await dialog.OpenAsync<ConfirmDialog>(
            $"{actionLabel} confirmation",
            new Dictionary<string, object?>
            {
                { "Category", category.ToUpperInvariant() },
                { "Title", $"{actionLabel} {itemName}?" },
                { "Subtitle", subtitle ?? $"{itemName} will be removed if not referenced." },
                { "ConfirmText", actionLabel },
                { "CancelText", "Cancel" },
                { "Intent", intent },
                { "Icon", icon }
            },
            new DialogOptions
            {
                Width = "440px",
                ShowClose = true,
                CloseDialogOnOverlayClick = false,
                CssClass = "mer-confirm-dialog"
            });
        return result is bool b && b;
    }

    public static Task<bool> ConfirmDeactivateAsync(this DialogService dialog, string itemName, string category = "ITEM") =>
        dialog.ConfirmDeleteAsync(itemName, category: category, actionLabel: "Deactivate", icon: "block");

    public static Task<bool> ConfirmUnmapAsync(this DialogService dialog, string itemName, string category = "MAPPING") =>
        dialog.ConfirmDeleteAsync(itemName, category: category, actionLabel: "Remove", icon: "link_off");
}
