using Radzen;

namespace MerinoOne.Web.Services;

public class ApiErrorNotifier
{
    private readonly NotificationService _notification;
    private readonly TokenAccessor _token;

    public ApiErrorNotifier(NotificationService notification, TokenAccessor token)
    {
        _notification = notification;
        _token = token;
    }

    public void Show(Exception ex)
    {
        if (ex is ApiException api) { ShowApi(api); return; }
        _notification.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Error,
            Summary = "Something went wrong",
            Detail = string.IsNullOrWhiteSpace(ex.Message)
                ? "An unexpected error occurred. Please try again or contact support."
                : ex.Message,
            Duration = 6000
        });
    }

    private void ShowApi(ApiException ex)
    {
        switch (ex.StatusCode)
        {
            case 0:
                _notification.Notify(new NotificationMessage
                {
                    Severity = NotificationSeverity.Error,
                    Summary = "Cannot reach server",
                    Detail = "Check your connection and try again.",
                    Duration = 6000
                });
                break;
            case 400:
            case 422:
                _notification.Notify(new NotificationMessage
                {
                    Severity = NotificationSeverity.Warning,
                    Summary = ex.Title,
                    Detail = ex.Errors.Length > 0 ? string.Join("\n", ex.Errors) : string.Empty,
                    Duration = 6000
                });
                break;
            case 401:
                _token.NotifySessionExpired();
                break;
            case 403:
                _notification.Notify(new NotificationMessage
                {
                    Severity = NotificationSeverity.Warning,
                    Summary = "Access denied",
                    Detail = "You don't have permission to perform this action.",
                    Duration = 5000
                });
                break;
            case 404:
                _notification.Notify(new NotificationMessage
                {
                    Severity = NotificationSeverity.Info,
                    Summary = "Not found",
                    Detail = ex.Title,
                    Duration = 5000
                });
                break;
            case >= 500:
                _notification.Notify(new NotificationMessage
                {
                    Severity = NotificationSeverity.Error,
                    Summary = "Something went wrong",
                    Detail = string.IsNullOrWhiteSpace(ex.TraceId)
                        ? "Please try again or contact support."
                        : $"Please contact support. Trace: {ex.TraceId}",
                    Duration = 8000
                });
                break;
            default:
                _notification.Notify(new NotificationMessage
                {
                    Severity = NotificationSeverity.Warning,
                    Summary = ex.Title,
                    Detail = ex.Errors.Length > 0 ? string.Join("\n", ex.Errors) : string.Empty,
                    Duration = 5000
                });
                break;
        }
    }
}
