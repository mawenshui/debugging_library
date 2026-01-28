using System.Net;
using FieldKb.Application.ImportExport;
using FieldKb.Client.Wpf;
using FieldKb.Infrastructure.ImportExport;
using FieldKb.Infrastructure.Sqlite;
using FieldKb.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FieldKb.Tests;

public sealed class LanExchangeApiTests
{
    [Fact]
    public async Task PingAndExport_Works_WithSharedKey()
    {
        var baseDir = CreateTempDir();
        try
        {
            var dbPath = Path.Combine(baseDir, "kb.sqlite");
            var factory = new SqliteConnectionFactory(new SqliteOptions { DatabasePath = dbPath });
            var store = new SqliteKbStore(factory);
            await store.InitializeAsync(CancellationToken.None);

            var identityDir = Path.Combine(baseDir, "identity");
            Directory.CreateDirectory(identityDir);
            var identityProvider = new InstanceIdentityProvider(identityDir);

            var service = new SqlitePackageTransferService(factory, identityProvider, new LocalInstanceContext(InstanceKind.Personal));

            var port = GetFreePort();
            var api = new LanExchangeApiHost(
                new LanExchangeOptions(port, "k"),
                new LocalInstanceContext(InstanceKind.Personal),
                identityProvider,
                service,
                NullLogger<LanExchangeApiHost>.Instance);

            await api.StartAsync(CancellationToken.None);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                client.DefaultRequestHeaders.Add("X-Lan-Key", "k");

                var ping = await client.GetAsync("/lan/ping");
                Assert.True(ping.IsSuccessStatusCode);

                var export = await client.GetAsync("/lan/export?mode=full&remoteInstanceId=remote");
                Assert.True(export.IsSuccessStatusCode);
                Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);
                var bytes = await export.Content.ReadAsByteArrayAsync();
                Assert.True(bytes.Length > 0);
            }
            finally
            {
                await api.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task Export_ReturnsUnauthorized_WhenSharedKeyMismatch()
    {
        var baseDir = CreateTempDir();
        try
        {
            var dbPath = Path.Combine(baseDir, "kb.sqlite");
            var factory = new SqliteConnectionFactory(new SqliteOptions { DatabasePath = dbPath });
            var store = new SqliteKbStore(factory);
            await store.InitializeAsync(CancellationToken.None);

            var identityDir = Path.Combine(baseDir, "identity");
            Directory.CreateDirectory(identityDir);
            var identityProvider = new InstanceIdentityProvider(identityDir);

            var service = new SqlitePackageTransferService(factory, identityProvider, new LocalInstanceContext(InstanceKind.Personal));

            var port = GetFreePort();
            var api = new LanExchangeApiHost(
                new LanExchangeOptions(port, "k"),
                new LocalInstanceContext(InstanceKind.Personal),
                identityProvider,
                service,
                NullLogger<LanExchangeApiHost>.Instance);

            await api.StartAsync(CancellationToken.None);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                client.DefaultRequestHeaders.Add("X-Lan-Key", "wrong");
                var export = await client.GetAsync("/lan/export?mode=full&remoteInstanceId=remote");
                Assert.Equal(HttpStatusCode.Unauthorized, export.StatusCode);
            }
            finally
            {
                await api.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FieldKb.Tests", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

