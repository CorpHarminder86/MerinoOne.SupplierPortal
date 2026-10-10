using MerinoOne.SupplierPortal.Application.Common.Models;
using MerinoOne.SupplierPortal.Application.SystemSettings.Display;
using MerinoOne.SupplierPortal.Contracts.SystemSettings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerinoOne.SupplierPortal.Controllers;

/// <summary>
/// The Display-category values every screen needs in order to render (today: money decimal places). Open to ANY
/// authenticated user — a supplier's pages format money too — which is why it is a controller of its own rather than
/// an action on <see cref="SystemSettingsController"/>: that is the admin Settings surface, and nothing on it should
/// ever be reachable without its permission policies. Changing the values still goes through
/// <c>POST api/system-settings</c> (<c>Settings.Write</c>).
/// </summary>
[ApiController]
[Authorize]
[Route("api/system-settings/display")]
public class DisplaySettingsController : ControllerBase
{
    private readonly IDisplaySettings _display;
    public DisplaySettingsController(IDisplaySettings display) => _display = display;

    [HttpGet]
    [EndpointSummary("Get display settings")]
    [EndpointDescription(@"The effective display preferences the UI formats with.
Returns: DisplaySettingsDto { moneyDecimalPlaces } — the saved Display.MoneyDecimalPlaces value, or the built-in default (4) when none is saved; always 0..6.
Open to any authenticated user (no permission policy) because every screen needs it to render money; changing it requires **Settings.Write** via POST api/system-settings.")]
    public async Task<Result<DisplaySettingsDto>> Get(CancellationToken ct)
    {
        var places = await _display.GetMoneyDecimalPlacesAsync(ct);
        return Result<DisplaySettingsDto>.Ok(new DisplaySettingsDto(places), HttpContext.TraceIdentifier);
    }
}
