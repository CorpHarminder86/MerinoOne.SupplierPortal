using MerinoOne.SupplierPortal.Application.Common.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

/// <summary>
/// Stage 1 mock file storage. Writes to the directory resolved from configuration:
/// <list type="number">
/// <item><c>FileStorage:LocalRoot</c> appsetting — absolute path used verbatim (e.g.
///   <c>D:\Uploads\merino</c>) or relative path resolved against <see cref="IHostEnvironment.ContentRootPath"/>
///   (e.g. <c>./uploads-dev</c>).</item>
/// <item>Fallback <c>{ContentRoot}/uploads</c> when the setting is blank.</item>
/// </list>
/// StorageKey is the relative path under the resolved root so we can swap the physical root
/// (or to Azure Blob) without rewriting persisted FileUrl values.
/// </summary>
internal sealed class LocalDiskFileStorageService : IFileStorageService
{
    private const string DefaultUploadsDirName = "uploads";
    private const string LocalRootConfigKey = "FileStorage:LocalRoot";

    private readonly IHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<LocalDiskFileStorageService> _logger;

    public LocalDiskFileStorageService(
        IHostEnvironment env,
        IConfiguration config,
        ILogger<LocalDiskFileStorageService> logger)
    {
        _env = env;
        _config = config;
        _logger = logger;
    }

    public async Task<StoredFile> StoreAsync(
        Stream content,
        string fileName,
        string mimeType,
        Guid scopeId,
        CancellationToken ct = default)
    {
        var sanitized = SanitizeFileName(fileName);
        var relativeKey = Path.Combine(scopeId.ToString("N"), $"{Guid.NewGuid():N}_{sanitized}")
                              .Replace('\\', '/');
        var absolutePath = ResolveAbsolutePath(relativeKey);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        await using (var fs = File.Create(absolutePath))
        {
            await content.CopyToAsync(fs, ct);
        }

        var size = new FileInfo(absolutePath).Length;
        _logger.LogInformation("[FileStorage:Local] Stored {File} ({Bytes} bytes) as {Key}",
            fileName, size, relativeKey);

        return new StoredFile(relativeKey, size);
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(storageKey)) return Task.FromResult<Stream?>(null);
        var absolutePath = ResolveAbsolutePath(storageKey);
        if (!File.Exists(absolutePath))
        {
            _logger.LogWarning("[FileStorage:Local] Missing file for key {Key} (resolved {Path}).",
                storageKey, absolutePath);
            return Task.FromResult<Stream?>(null);
        }
        Stream stream = File.OpenRead(absolutePath);
        return Task.FromResult<Stream?>(stream);
    }

    private string ResolveAbsolutePath(string relativeKey)
    {
        var root = ResolveUploadsRoot();
        // Defend against ../traversal — the key is constructed server-side but we re-resolve from
        // GET requests too, where a malicious caller might tamper with it.
        var combined = Path.GetFullPath(Path.Combine(root, relativeKey));
        if (!combined.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Storage key escapes uploads root: {relativeKey}");
        }
        return combined;
    }

    /// <summary>
    /// Resolves the on-disk root directory uploads live under. Reads
    /// <c>FileStorage:LocalRoot</c> from configuration first; falls back to
    /// <c>{ContentRoot}/uploads</c> when the setting is blank.
    /// </summary>
    private string ResolveUploadsRoot()
    {
        var configured = _config[LocalRootConfigKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(_env.ContentRootPath, DefaultUploadsDirName);
        }
        // Absolute path (e.g. "D:\\Uploads\\merino") used as-is; relative path
        // (e.g. "./uploads-dev") resolved against ContentRoot.
        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(_env.ContentRootPath, configured));
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "file";
        var trimmed = Path.GetFileName(fileName.Trim());
        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(bad, '_');
        }
        return trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }
}
