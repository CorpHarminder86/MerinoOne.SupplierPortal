namespace MerinoOne.SupplierPortal.Application.Common.Interfaces;

/// <summary>
/// Mock-then-Live file storage seam. Stage 1 implementation is local disk
/// (<c>{ContentRoot}/uploads/{seccodeOrInviteId}/{guid}_{sanitizedFileName}</c>);
/// Stage 2 swaps to Azure Blob without touching callsites.
/// </summary>
public interface IFileStorageService
{
    /// <summary>
    /// Persists <paramref name="content"/> to durable storage and returns a stable
    /// <see cref="StoredFile.StorageKey"/> the caller writes to <c>DocumentUpload.FileUrl</c>.
    /// </summary>
    Task<StoredFile> StoreAsync(
        Stream content,
        string fileName,
        string mimeType,
        Guid scopeId,
        CancellationToken ct = default);

    /// <summary>
    /// Opens a read stream for a previously-stored file by its <paramref name="storageKey"/>.
    /// Returns <c>null</c> if the underlying bytes are missing (caller decides 404 vs error).
    /// </summary>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default);
}

/// <summary>Result of <see cref="IFileStorageService.StoreAsync"/>.</summary>
/// <param name="StorageKey">Implementation-private key — opaque to callers; pass back to OpenReadAsync.</param>
/// <param name="SizeBytes">Number of bytes actually written.</param>
public sealed record StoredFile(string StorageKey, long SizeBytes);
