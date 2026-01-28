using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FieldKb.Application.Abstractions;
using FieldKb.Application.ImportExport;
using Microsoft.Extensions.Logging;

namespace FieldKb.Client.Wpf;

public partial class LanExchangeViewModel : ObservableObject
{
    private readonly LanExchangeApiHost _apiHost;
    private readonly LocalInstanceContext _localInstanceContext;
    private readonly IPackageTransferService _packageTransferService;
    private readonly ILogger<LanExchangeViewModel> _logger;
    private readonly Action _close;
    private readonly Action _onImported;
    private readonly string _localInstanceId;

    public LanExchangeViewModel(
        LanExchangeApiHost apiHost,
        LocalInstanceContext localInstanceContext,
        IPackageTransferService packageTransferService,
        ILogger<LanExchangeViewModel> logger,
        string localInstanceId,
        string initialRemoteInstanceId,
        Action close,
        Action onImported)
    {
        _apiHost = apiHost;
        _localInstanceContext = localInstanceContext;
        _packageTransferService = packageTransferService;
        _logger = logger;
        _localInstanceId = localInstanceId;
        _close = close;
        _onImported = onImported;

        RemoteInstanceId = string.IsNullOrWhiteSpace(initialRemoteInstanceId) ? "corporate" : initialRemoteInstanceId;

        UpdateLocalTexts();
    }

    [ObservableProperty]
    private string _remoteBaseUrl = "127.0.0.1:5123";

    [ObservableProperty]
    private string _remoteInstanceId = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _localInfoText = string.Empty;

    [ObservableProperty]
    private string _localUrlsText = string.Empty;

    [ObservableProperty]
    private string _localAuthText = string.Empty;

    [RelayCommand]
    private void Close()
    {
        _close();
    }

    [RelayCommand]
    private async Task PingAsync()
    {
        try
        {
            StatusText = "正在测试连接…";
            var baseUrl = NormalizeBaseUrl(RemoteBaseUrl);
            using var client = CreateClient(baseUrl);

            var json = await client.GetStringAsync("/lan/ping");
            StatusText = $"连接成功：{json}";
        }
        catch (Exception ex)
        {
            StatusText = $"连接失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private Task PullIncrementalAsync() => PullAsync(ExportMode.Incremental);

    [RelayCommand]
    private Task PullFullAsync() => PullAsync(ExportMode.Full);

    [RelayCommand]
    private Task PushIncrementalAsync() => PushAsync(ExportMode.Incremental);

    [RelayCommand]
    private Task PushFullAsync() => PushAsync(ExportMode.Full);

    private async Task PullAsync(ExportMode mode)
    {
        try
        {
            var baseUrl = NormalizeBaseUrl(RemoteBaseUrl);
            var remoteId = string.IsNullOrWhiteSpace(RemoteInstanceId) ? "corporate" : RemoteInstanceId;
            StatusText = mode == ExportMode.Incremental ? "拉取导入（增量）：准备请求对端导出…" : "拉取导入（全量）：准备请求对端导出…";

            using var client = CreateClient(baseUrl);
            var modeText = mode == ExportMode.Incremental ? "incremental" : "full";
            var url = $"/lan/export?mode={modeText}&remoteInstanceId={Uri.EscapeDataString(remoteId)}";

            var bytes = await client.GetByteArrayAsync(url);

            var tempDir = CreateTempDirectory();
            var zipPath = Path.Combine(tempDir, $"pull_{modeText}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.zip");
            await File.WriteAllBytesAsync(zipPath, bytes);

            StatusText = "拉取完成：正在导入到本机…";
            var report = await _packageTransferService.ImportAsync(zipPath, CancellationToken.None);
            StatusText = $"导入完成：导入 {report.ImportedCount}，跳过 {report.SkippedCount}，冲突 {report.ConflictCount}。";
            _onImported();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            StatusText = "拉取失败：鉴权失败（共享密钥不一致）。";
        }
        catch (Exception ex)
        {
            StatusText = $"拉取失败：{ex.Message}";
        }
    }

    private async Task PushAsync(ExportMode mode)
    {
        try
        {
            var baseUrl = NormalizeBaseUrl(RemoteBaseUrl);
            var remoteId = string.IsNullOrWhiteSpace(RemoteInstanceId) ? "corporate" : RemoteInstanceId;
            StatusText = mode == ExportMode.Incremental ? "推送导入（增量）：本机导出中…" : "推送导入（全量）：本机导出中…";

            var tempDir = CreateTempDirectory();
            var export = await _packageTransferService.ExportAsync(
                new ExportRequest(tempDir, remoteId, mode, UpdatedAfterUtc: null, Limit: null),
                CancellationToken.None);

            if (!File.Exists(export.PackagePath))
            {
                StatusText = "推送失败：导出包不存在。";
                return;
            }

            StatusText = "推送中：正在上传到对端并触发导入…";

            using var client = CreateClient(baseUrl);
            await using var fs = new FileStream(export.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var content = new StreamContent(fs);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

            var resp = await client.PostAsync("/lan/import", content);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                StatusText = "推送失败：鉴权失败（共享密钥不一致）。";
                return;
            }
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            StatusText = $"推送完成：对端返回 {body}";
        }
        catch (Exception ex)
        {
            StatusText = $"推送失败：{ex.Message}";
            _logger.LogError(ex, "推送导入失败。");
        }
    }

    private void UpdateLocalTexts()
    {
        LocalInfoText = $"实例类型：{_localInstanceContext.Kind}；实例ID：{_localInstanceId}";

        var port = _apiHost.Port;
        var urls = new List<string>
        {
            $"http://127.0.0.1:{port}",
            $"http://localhost:{port}"
        };

        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            {
                if (IPAddress.IsLoopback(ip))
                {
                    continue;
                }
                urls.Add($"http://{ip}:{port}");
            }
        }
        catch
        {
        }

        LocalUrlsText = string.Join(Environment.NewLine, urls.Distinct());
        LocalAuthText = _apiHost.SharedKey is null ? "鉴权：未启用（共享密钥为空）" : "鉴权：已启用（需要共享密钥）";
    }

    private HttpClient CreateClient(string baseUrl)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        if (_apiHost.SharedKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Lan-Key", _apiHost.SharedKey);
        }
        return client;
    }

    private static string NormalizeBaseUrl(string input)
    {
        var text = input?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return "http://127.0.0.1:5123";
        }

        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            text = "http://" + text;
        }

        return text.EndsWith("/", StringComparison.Ordinal) ? text[..^1] : text;
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DebugSummaryPlatform_LanExchange", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
