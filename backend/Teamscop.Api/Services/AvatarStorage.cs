using Microsoft.Extensions.Options;
using Teamscop.Api.Options;

namespace Teamscop.Api.Services;

public interface IAvatarStorage
{
    Task<string?> SaveAsync(Guid ownerId, IFormFile? file, CancellationToken cancellationToken);
}

public sealed class AvatarStorage(IOptions<StorageOptions> options, IWebHostEnvironment env) : IAvatarStorage
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    private readonly StorageOptions _options = options.Value;

    public async Task<string?> SaveAsync(Guid ownerId, IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return null;
        }

        if (file.Length > 5 * 1024 * 1024)
        {
            throw new InvalidOperationException("Avatar must be 5MB or smaller.");
        }

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext) || !AllowedExtensions.Contains(ext))
        {
            throw new InvalidOperationException("Avatar must be jpg, png, webp, or gif.");
        }

        var root = Path.IsPathRooted(_options.AvatarRoot)
            ? _options.AvatarRoot
            : Path.Combine(env.ContentRootPath, _options.AvatarRoot);
        Directory.CreateDirectory(root);

        var fileName = $"{ownerId:N}{ext.ToLowerInvariant()}";
        var fullPath = Path.Combine(root, fileName);
        await using (var stream = File.Create(fullPath))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        return $"{_options.PublicAvatarBasePath.TrimEnd('/')}/{fileName}";
    }
}
