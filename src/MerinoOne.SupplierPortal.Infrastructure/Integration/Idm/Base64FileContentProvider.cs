using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using MerinoOne.SupplierPortal.Application.Integration.Idm;

namespace MerinoOne.SupplierPortal.Infrastructure.Integration.Idm;

/// <summary>
/// R8 (2026-07-04) — TSD R8 §6 / D-R8-18. Thin wrapper over <see cref="IFileStorageService"/>: reads the stored
/// file and returns its base64. Returns null when the bytes are missing (a gone file drives a Validation-class
/// failure, mirroring the observed IDM 400 <c>File name "null"</c>). Whole-file-in-memory is accepted; the
/// dispatcher's concurrency cap bounds the number of simultaneous encodings.
/// </summary>
public sealed class Base64FileContentProvider : IFileContentProvider
{
    private readonly IFileStorageService _storage;

    public Base64FileContentProvider(IFileStorageService storage) => _storage = storage;

    public async Task<string?> ToBase64Async(string storageKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storageKey)) return null;
        await using var stream = await _storage.OpenReadAsync(storageKey, ct);
        if (stream is null) return null;

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return Convert.ToBase64String(ms.ToArray());
    }
}
