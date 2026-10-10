using System.Text;

namespace MerinoOne.SupplierPortal.Contracts.PurchaseOrders;

/// <summary>
/// Display labels for the PoStatus enum NAME that the PO DTOs carry as a string (Contracts cannot reference the Domain
/// enum). Humanizes the PascalCase name ("PartiallyDelivered" → "Partially Delivered") with one business rename: a
/// <c>Released</c> PO is awaiting the supplier's response, so it reads "Pending". A null/blank status shows an em dash.
/// </summary>
public static class PoStatusLabels
{
    private const string ReleasedName = "Released";
    private const string PendingLabel = "Pending";
    private const string NoStatus = "\u2014";   // em dash

    public static string Label(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return NoStatus;

        var name = status.Trim();
        return string.Equals(name, ReleasedName, StringComparison.OrdinalIgnoreCase) ? PendingLabel : Humanize(name);
    }

    // A space goes before an upper-case letter that follows a lower-case letter or digit ("PartiallyDelivered"), or that
    // starts a new word right after an acronym ("ASNReady" → "ASN Ready"). Already-spaced names pass through unchanged.
    private static string Humanize(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c))
            {
                var prev = name[i - 1];
                var startsWord = char.IsLower(prev) || char.IsDigit(prev)
                                 || (char.IsUpper(prev) && i + 1 < name.Length && char.IsLower(name[i + 1]));
                if (startsWord) sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
