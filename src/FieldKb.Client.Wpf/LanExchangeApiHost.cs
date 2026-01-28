using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using FieldKb.Application.Abstractions;
using FieldKb.Application.ImportExport;
using FieldKb.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FieldKb.Client.Wpf;

public sealed class LanExchangeApiHost : IHostedService
{
    private readonly LanExchangeOptions _options;
    private readonly LocalInstanceContext _localInstanceContext;
    private readonly InstanceIdentityProvider _identityProvider;
    private readonly IPackageTransferService _packageTransferService;
    private readonly ILogger<LanExchangeApiHost> _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private string _instanceId = string.Empty;
    private string? _sharedKey;

    public LanExchangeApiHost(
        LanExchangeOptions options,
        LocalInstanceContext localInstanceContext,
        InstanceIdentityProvider identityProvider,
        IPackageTransferService packageTransferService,
        ILogger<LanExchangeApiHost> logger)
    {
        _options = options;
        _localInstanceContext = localInstanceContext;
        _identityProvider = identityProvider;
        _packageTransferService = packageTransferService;
        _logger = logger;
        _sharedKey = NormalizeSharedKey(options.SharedKey);
    }

    public int Port => _options.Port;

    public string? SharedKey => _sharedKey;

    public void UpdateSharedKey(string? sharedKey)
    {
        _sharedKey = NormalizeSharedKey(sharedKey);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var identity = await _identityProvider.GetOrCreateAsync(_localInstanceContext.Kind, cancellationToken);
            _instanceId = identity.InstanceId;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listener = new TcpListener(IPAddress.Any, _options.Port);
            _listener.Start();

            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
            _logger.LogInformation("局域网交换服务已启动：0.0.0.0:{Port}（SharedKey={HasKey}）", _options.Port, SharedKey is not null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "局域网交换服务启动失败（Port={Port}）。", _options.Port);
            await StopAsync(CancellationToken.None);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("局域网交换服务停止中：Port={Port}", _options.Port);
        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch
            {
            }
        }

        _acceptLoop = null;
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _logger.LogInformation("局域网交换服务已停止：Port={Port}", _options.Port);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                return;
            }
            catch (ObjectDisposedException)
            {
                client?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                _logger.LogError(ex, "局域网交换服务 Accept 失败。");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        client.ReceiveTimeout = 15_000;
        client.SendTimeout = 15_000;
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var sw = Stopwatch.StartNew();

        try
        {
            using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, ct);
            if (request is null)
            {
                return;
            }

            _logger.LogInformation("局域网交换请求：{Remote} {Method} {Target}", remote, request.Method, request.Target);

            if (!TryAuthorize(request.Headers, out var unauthorizedBody))
            {
                _logger.LogWarning("局域网交换鉴权失败：{Remote} {Method} {Target}", remote, request.Method, request.Target);
                await WriteResponseAsync(stream, 401, "application/json; charset=utf-8", unauthorizedBody, ct);
                return;
            }

            var uri = new Uri("http://localhost" + request.Target, UriKind.Absolute);
            var path = uri.AbsolutePath;

            if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) && path == "/lan/ping")
            {
                var body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    ok = true,
                    instanceId = _instanceId,
                    instanceKind = _localInstanceContext.Kind.ToString()
                });
                await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", body, ct);
                _logger.LogInformation("局域网交换响应：{Remote} ping ok {ElapsedMs}ms", remote, sw.ElapsedMilliseconds);
                return;
            }

            if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) && path == "/lan/export")
            {
                var remoteInstanceId = GetQueryValue(uri, "remoteInstanceId");
                if (string.IsNullOrWhiteSpace(remoteInstanceId))
                {
                    await WriteResponseAsync(stream, 400, "application/json; charset=utf-8", JsonBytes(new { error = "remoteInstanceId is required" }), ct);
                    _logger.LogWarning("局域网交换导出请求参数缺失：{Remote} remoteInstanceId 为空", remote);
                    return;
                }

                var modeText = GetQueryValue(uri, "mode");
                var mode = string.Equals(modeText, "incremental", StringComparison.OrdinalIgnoreCase) ? ExportMode.Incremental : ExportMode.Full;

                var tempDir = CreateTempDirectory();
                _logger.LogInformation("局域网交换导出开始：{Remote} 模式={Mode} 对端标识={RemoteId}", remote, mode, remoteInstanceId);
                var result = await _packageTransferService.ExportAsync(
                    new ExportRequest(tempDir, remoteInstanceId, mode, UpdatedAfterUtc: null, Limit: null),
                    ct);

                if (!File.Exists(result.PackagePath))
                {
                    await WriteResponseAsync(stream, 500, "application/json; charset=utf-8", JsonBytes(new { error = "export failed" }), ct);
                    _logger.LogError("局域网交换导出失败：{Remote} 未生成包文件", remote);
                    return;
                }

                var zip = await File.ReadAllBytesAsync(result.PackagePath, ct);
                await WriteResponseAsync(stream, 200, "application/zip", zip, ct);
                _logger.LogInformation("局域网交换导出完成：{Remote} 字节={Bytes} 用时={ElapsedMs}ms", remote, zip.Length, sw.ElapsedMilliseconds);
                return;
            }

            if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) && path == "/lan/import")
            {
                if (request.Body is null || request.Body.Length == 0)
                {
                    await WriteResponseAsync(stream, 400, "application/json; charset=utf-8", JsonBytes(new { error = "empty body" }), ct);
                    _logger.LogWarning("局域网交换导入失败：{Remote} 空请求体", remote);
                    return;
                }

                var tempDir = CreateTempDirectory();
                var zipPath = Path.Combine(tempDir, $"lan_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.zip");
                await File.WriteAllBytesAsync(zipPath, request.Body, ct);

                _logger.LogInformation("局域网交换导入开始：{Remote} 字节={Bytes}", remote, request.Body.Length);
                var report = await _packageTransferService.ImportAsync(zipPath, ct);
                var body = JsonBytes(new
                {
                    imported = report.ImportedCount,
                    skipped = report.SkippedCount,
                    conflicts = report.ConflictCount
                });

                await WriteResponseAsync(stream, 200, "application/json; charset=utf-8", body, ct);
                _logger.LogInformation("局域网交换导入完成：{Remote} 导入={Imported} 跳过={Skipped} 冲突={Conflicts} 用时={ElapsedMs}ms",
                    remote, report.ImportedCount, report.SkippedCount, report.ConflictCount, sw.ElapsedMilliseconds);
                return;
            }

            await WriteResponseAsync(stream, 404, "application/json; charset=utf-8", JsonBytes(new { error = "not found" }), ct);
            _logger.LogWarning("局域网交换 404：{Remote} {Method} {Target}", remote, request.Method, request.Target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "局域网交换请求处理失败：{Remote} 用时={ElapsedMs}ms", remote, sw.ElapsedMilliseconds);
        }
    }

    private bool TryAuthorize(IReadOnlyDictionary<string, string> headers, out byte[] unauthorizedJson)
    {
        unauthorizedJson = JsonBytes(new { error = "unauthorized" });
        var required = SharedKey;
        if (required is null)
        {
            return true;
        }

        if (!headers.TryGetValue("X-Lan-Key", out var provided))
        {
            return false;
        }

        return string.Equals(provided, required, StringComparison.Ordinal);
    }

    private static async Task<HttpRequestData?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var received = 0;
        var headerEnd = -1;
        using var ms = new MemoryStream();

        while (headerEnd < 0)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (n <= 0)
            {
                return null;
            }

            ms.Write(buffer, 0, n);
            received += n;

            if (received > 256 * 1024)
            {
                return null;
            }

            var data = ms.GetBuffer();
            headerEnd = IndexOfHeaderEnd(data, (int)ms.Length);
        }

        var allBytes = ms.GetBuffer();
        var allLen = (int)ms.Length;
        var headerBytes = new byte[headerEnd];
        Array.Copy(allBytes, 0, headerBytes, 0, headerEnd);
        var remainingLen = allLen - (headerEnd + 4);
        var remaining = remainingLen > 0 ? new byte[remainingLen] : Array.Empty<byte>();
        if (remainingLen > 0)
        {
            Array.Copy(allBytes, headerEnd + 4, remaining, 0, remainingLen);
        }

        var headerText = Encoding.ASCII.GetString(headerBytes);
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0)
        {
            return null;
        }

        var first = lines[0].Split(' ');
        if (first.Length < 2)
        {
            return null;
        }

        var method = first[0];
        var target = first[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var idx = line.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            headers[key] = value;
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var clText) && int.TryParse(clText, out var cl))
        {
            contentLength = Math.Max(0, cl);
        }

        byte[]? body = null;
        if (contentLength > 0)
        {
            body = new byte[contentLength];
            var offset = 0;

            var pre = remaining.Length;
            if (pre > 0)
            {
                var copy = Math.Min(pre, contentLength);
                Array.Copy(remaining, 0, body, 0, copy);
                offset += copy;
            }

            while (offset < contentLength)
            {
                var n = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), ct);
                if (n <= 0)
                {
                    break;
                }
                offset += n;
            }
        }

        return new HttpRequestData(method, target, headers, body);
    }

    private static int IndexOfHeaderEnd(byte[] data, int len)
    {
        for (var i = 0; i + 3 < len; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
            {
                return i;
            }
        }
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string contentType, byte[] body, CancellationToken ct)
    {
        var status = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            _ => "Internal Server Error"
        };

        var header =
            $"HTTP/1.1 {statusCode} {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            $"Connection: close\r\n" +
            $"\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        var q = uri.Query;
        if (string.IsNullOrEmpty(q))
        {
            return null;
        }

        if (q.StartsWith("?", StringComparison.Ordinal))
        {
            q = q[1..];
        }

        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 0)
            {
                continue;
            }

            var k = Uri.UnescapeDataString(kv[0]);
            if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
        }

        return null;
    }

    private static byte[] JsonBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static string? NormalizeSharedKey(string? sharedKey)
    {
        var normalized = (sharedKey ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DebugSummaryPlatform_LanExchange", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed record HttpRequestData(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[]? Body);
}
