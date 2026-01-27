using FieldKb.Client.Wpf;

namespace FieldKb.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task JsonAppSettingsStore_ReadWrite_ProfessionAndUserName()
    {
        var baseDir = CreateTempDir();
        try
        {
            var path = Path.Combine(baseDir, "appsettings.json");
            var store = new JsonAppSettingsStore(path);

            await store.WriteUserNameAsync("测试用户", CancellationToken.None);
            await store.WriteProfessionIdAsync(ProfessionIds.Software, CancellationToken.None);

            var user = await store.ReadUserNameAsync(CancellationToken.None);
            var prof = await store.ReadProfessionIdAsync(CancellationToken.None);

            Assert.Equal("测试用户", user);
            Assert.Equal(ProfessionIds.Software, ProfessionIds.Normalize(prof));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task JsonAppSettingsStore_BackwardCompatible_WhenProfessionMissing()
    {
        var baseDir = CreateTempDir();
        try
        {
            var path = Path.Combine(baseDir, "appsettings.json");
            await File.WriteAllTextAsync(path, "{ \"User\": { \"Name\": \"old\" } }", CancellationToken.None);

            var store = new JsonAppSettingsStore(path);
            var user = await store.ReadUserNameAsync(CancellationToken.None);
            var prof = await store.ReadProfessionIdAsync(CancellationToken.None);

            Assert.Equal("old", user);
            Assert.Null(prof);
            Assert.Equal(ProfessionIds.General, ProfessionIds.Normalize(prof));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FieldKb.Tests", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

