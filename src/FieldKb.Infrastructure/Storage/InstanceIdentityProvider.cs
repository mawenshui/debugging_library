using System.IO;
using System.Text.Json;
using FieldKb.Application.ImportExport;

namespace FieldKb.Infrastructure.Storage;

public sealed class InstanceIdentityProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string? _baseDirectory;

    public InstanceIdentityProvider(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory;
    }

    public async Task<InstanceIdentity> GetOrCreateAsync(InstanceKind kind, CancellationToken cancellationToken)
    {
        var dir = _baseDirectory ?? AppDataPaths.GetAppDataDirectory();
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "instance.json");
        if (File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            var existing = await JsonSerializer.DeserializeAsync<InstanceIdentity>(stream, JsonOptions, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }
        }

        var identity = new InstanceIdentity(
            InstanceId: Guid.NewGuid().ToString("D"),
            Kind: kind,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        await using (var stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(stream, identity, JsonOptions, cancellationToken);
        }

        return identity;
    }
}
