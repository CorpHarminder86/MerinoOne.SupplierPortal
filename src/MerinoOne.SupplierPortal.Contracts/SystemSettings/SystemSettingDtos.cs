namespace MerinoOne.SupplierPortal.Contracts.SystemSettings;

/// <summary>
/// Wire model for a single setting row. <c>SettingValue</c> is masked for sensitive keys
/// (e.g. EmailConfig.Password becomes "********"); the original ciphertext never leaves the API.
/// <c>DefaultValueString</c> mirrors the seed default so the UI can render a "reset" affordance
/// without round-tripping; <c>DefaultValueInt</c> is populated when the default parses as an integer.
/// </summary>
public record SystemSettingDto(
    Guid Id,
    int Seq,
    string Category,
    string SettingKey,
    string SettingValue,
    string? Description,
    bool IsActive,
    string? DefaultValueString,
    int? DefaultValueInt,
    DateTime CreatedOn,
    DateTime? UpdatedOn);

public record SaveSystemSettingRequest(string Category, string Key, string Value);

public record ResetSystemSettingRequest(string Category, string Key);

public record SendTestEmailRequest(string? ToEmail);

public record TestEmailResult(bool Success, string Message);
