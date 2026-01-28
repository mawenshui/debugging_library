using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FieldKb.Application.Abstractions;
using FieldKb.Application.ImportExport;
using FieldKb.Infrastructure.BulkExport;
using FieldKb.Infrastructure.ImportExport;
using FieldKb.Infrastructure.SpreadsheetImport;
using FieldKb.Infrastructure.Sqlite;
using FieldKb.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FieldKb.Client.Wpf;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private BootstrapWindow? _bootstrapWindow;
    private CancellationTokenSource? _startupCts;
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activateEvent;
    private static CancellationTokenSource? _singleInstanceCts;
    private static int _pendingActivate;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        TryAppendBootstrapLog("启动入口：OnStartup。");

        if (!EnsureSingleInstance())
        {
            try
            {
                MessageBox.Show("程序已在运行，已尝试激活已打开的窗口。若仍未看到界面，请检查是否被最小化或在其他桌面/屏幕。", "调试资料汇总平台");
            }
            catch
            {
            }
            Shutdown();
            return;
        }

        try
        {
            _bootstrapWindow = new BootstrapWindow();
            _bootstrapWindow.Show();
            _bootstrapWindow.Activate();
        }
        catch (Exception ex)
        {
            TryAppendBootstrapLog($"启动失败：无法显示启动窗口。{ex.GetType().Name} {ex.Message}");
            try
            {
                MessageBox.Show($"启动失败：无法显示启动窗口。\n\n{ex.Message}", "调试资料汇总平台");
            }
            catch
            {
            }
            Environment.Exit(1);
        }

        _startupCts = new CancellationTokenSource();
        _ = Task.Run(() => StartHostAndInitializeAsync(_startupCts.Token), CancellationToken.None);
        _ = StartUiWatchdogAsync(_startupCts.Token);
    }

    private async Task StartHostAndInitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            TryAppendBootstrapLog("启动流程：准备配置与数据目录。");
            await Task.Run(MigrateConfigIfNeeded, cancellationToken);

            var logDir = AppDataPaths.GetLogsDirectory();
            Directory.CreateDirectory(logDir);
            var sessionLogPath = Path.Combine(logDir, $"FieldKb_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.log");
            TryAppendBootstrapLog($"启动流程：准备日志。日志文件={sessionLogPath}");

            var host = BuildHost(sessionLogPath);
            _host = host;
            host.Start();

            var logger = host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("日志文件：{Path}", sessionLogPath);
            logger.LogInformation("数据库文件：{Path}", host.Services.GetRequiredService<SqliteOptions>().DatabasePath);
            logger.LogInformation("附件目录：{Path}", AppDataPaths.GetAttachmentsDirectory());
            logger.LogInformation("配置目录：{Path}", AppDataPaths.GetConfigDirectory());

            await Dispatcher.InvokeAsync(() =>
            {
                DispatcherUnhandledException += (_, args) =>
                {
                    logger.LogError(args.Exception, "发生未处理 UI 异常。");
                    args.Handled = true;
                    TryAppendBootstrapLog($"未处理 UI 异常：{args.Exception.GetType().Name} {args.Exception.Message}");
                    _ = Task.Run(() =>
                    {
                        Thread.Sleep(300);
                        Environment.Exit(1);
                    });
                };
            });

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    logger.LogCritical(ex, "发生未处理异常，程序即将退出。");
                }
            };

            await InitializeAndShowAsync(logger);

            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    _bootstrapWindow?.Close();
                }
                catch
                {
                }
                _bootstrapWindow = null;
            });
        }
        catch (Exception ex)
        {
            TryAppendBootstrapLog($"启动失败：{ex.GetType().Name} {ex.Message}");
            try
            {
                await Dispatcher.InvokeAsync(() =>
                    MessageBox.Show($"启动失败：{ex.Message}\n\n可查看 %TEMP%\\FieldKb_bootstrap.log 或 data\\logs 下最新日志。", "调试资料汇总平台"));
            }
            catch
            {
            }
            Environment.Exit(1);
        }
    }

    private static IHost BuildHost(string sessionLogPath)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((context, config) =>
            {
                var configDir = AppDataPaths.GetConfigDirectory();
                Directory.CreateDirectory(configDir);

                config.AddJsonFile(AppDataPaths.GetDefaultAppSettingsPath(), optional: true, reloadOnChange: false);
                config.AddJsonFile(AppDataPaths.GetAppSettingsPath(), optional: true, reloadOnChange: false);
            })
            .ConfigureServices((context, services) =>
            {
                var dbPathSetting = context.Configuration["Storage:DatabasePath"];
                var dbPath = string.IsNullOrWhiteSpace(dbPathSetting)
                    ? AppDataPaths.GetDatabasePath()
                    : Path.IsPathRooted(dbPathSetting)
                        ? Path.Combine(AppDataPaths.GetBaseDirectory(), Path.GetFileName(dbPathSetting))
                        : Path.Combine(AppDataPaths.GetBaseDirectory(), dbPathSetting);
                Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

                services.AddSingleton(new SqliteOptions { DatabasePath = dbPath });
                services.AddSingleton<SqliteConnectionFactory>();
                services.AddSingleton<IKbStore, SqliteKbStore>();

                var instanceKindText = context.Configuration["App:InstanceKind"];
                var instanceKind = Enum.TryParse<InstanceKind>(instanceKindText, ignoreCase: true, out var parsed)
                    ? parsed
                    : InstanceKind.Personal;

                services.AddSingleton(new LocalInstanceContext(instanceKind));
                services.AddSingleton(new InstanceIdentityProvider(AppDataPaths.GetConfigDirectory()));
                services.AddSingleton<IPackageTransferService, SqlitePackageTransferService>();
                services.AddSingleton<IBulkExportService, SqliteBulkExportService>();
                services.AddSingleton<ISpreadsheetImportService, XlsxSpreadsheetImportService>();
                services.AddSingleton<IUiDialogService, WpfDialogService>();
                services.AddSingleton<ProfessionProfileProvider>();

                var lanPortText = context.Configuration["LanExchange:Port"];
                var lanPort = int.TryParse(lanPortText, out var portParsed) ? portParsed : 5123;
                var lanSharedKey = context.Configuration["LanExchange:SharedKey"];
                services.AddSingleton(new LanExchangeOptions(lanPort, lanSharedKey));
                services.AddSingleton<LanExchangeApiHost>();
                services.AddHostedService(sp => sp.GetRequiredService<LanExchangeApiHost>());

                var logWriter = new FileLogWriter(sessionLogPath);
                var logStore = new AppLogStore(sessionLogPath);
                services.AddSingleton<IAppLogStore>(logStore);
                services.AddLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.SetMinimumLevel(LogLevel.Information);
                    logging.AddFilter("Microsoft", LogLevel.Warning);
                    logging.AddProvider(new FileAndMemoryLoggerProvider(logStore, logWriter, LogLevel.Information));
                });

                services.AddSingleton<IAppSettingsStore>(_ => new JsonAppSettingsStore(AppDataPaths.GetAppSettingsPath()));
                services.AddSingleton<OperationPasswordService>();
                services.AddSingleton<IProfessionFixedFieldSettings, ProfessionFixedFieldSettingsService>();
                services.AddSingleton<IProfessionProfileProvider>(sp =>
                    new CustomizableProfessionProfileProvider(
                        sp.GetRequiredService<ProfessionProfileProvider>(),
                        sp.GetRequiredService<IProfessionFixedFieldSettings>()));
                var configuredUserName = context.Configuration["User:Name"];
                var initialUserName = UserNameRules.Normalize(configuredUserName);
                if (!UserNameRules.IsValid(initialUserName, out _))
                {
                    initialUserName = UserNameRules.Normalize(Environment.UserName);
                }
                var configuredProfession = context.Configuration["User:Profession"];
                var initialProfession = ProfessionIds.Normalize(configuredProfession);
                services.AddSingleton<IUserContext>(new UserContext(initialUserName, initialProfession));

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    private async Task InitializeAndShowAsync(ILogger logger)
    {
        try
        {
            logger.LogInformation("应用启动：初始化开始。");

            await Dispatcher.InvokeAsync(() =>
            {
                var mainWindow = _host!.Services.GetRequiredService<MainWindow>();
                MainWindow = mainWindow;
                mainWindow.Show();
                try
                {
                    _bootstrapWindow?.Close();
                }
                catch
                {
                }
                _bootstrapWindow = null;
                if (Interlocked.Exchange(ref _pendingActivate, 0) == 1)
                {
                    TryActivateWindow(mainWindow);
                }
            });

            var store = _host!.Services.GetRequiredService<IKbStore>();
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            await _host!.Services.GetRequiredService<IProfessionFixedFieldSettings>()
                .InitializeAsync(CancellationToken.None)
                .ConfigureAwait(false);

            await _host!.Services.GetRequiredService<OperationPasswordService>()
                .InitializeAsync(CancellationToken.None)
                .ConfigureAwait(false);

            var mainVm = _host!.Services.GetRequiredService<MainViewModel>();
            await mainVm.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            logger.LogInformation("应用启动：初始化完成。");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "应用初始化失败，程序即将退出。");
            await Dispatcher.InvokeAsync(Shutdown);
            Environment.Exit(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _startupCts?.Cancel();
        }
        catch
        {
        }
        finally
        {
            _startupCts?.Dispose();
            _startupCts = null;
        }

        try
        {
            _bootstrapWindow?.Close();
        }
        catch
        {
        }
        _bootstrapWindow = null;

        var host = _host;
        _host = null;

        if (host is not null)
        {
            var timeout = TimeSpan.FromSeconds(5);
            var sw = Stopwatch.StartNew();

            try
            {
                using var cts = new CancellationTokenSource(timeout);
                var stopTask = host.StopAsync(cts.Token);
                if (!stopTask.Wait(timeout))
                {
                    Environment.Exit(0);
                }
            }
            catch
            {
            }

            var remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                Environment.Exit(0);
            }

            try
            {
                var disposeTask = Task.Run(host.Dispose);
                if (!disposeTask.Wait(remaining))
                {
                    Environment.Exit(0);
                }
            }
            catch
            {
            }
        }

        try
        {
            _singleInstanceCts?.Cancel();
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
        }
        finally
        {
            _activateEvent?.Dispose();
            _activateEvent = null;
            _singleInstanceCts?.Dispose();
            _singleInstanceCts = null;
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    private static void MigrateConfigIfNeeded()
    {
        var configDir = AppDataPaths.GetConfigDirectory();
        Directory.CreateDirectory(configDir);

        var userSettingsPath = AppDataPaths.GetAppSettingsPath();
        if (!File.Exists(userSettingsPath))
        {
            var legacySettingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FieldKb",
                "config",
                "appsettings.json");
            if (File.Exists(legacySettingsPath))
            {
                File.Copy(legacySettingsPath, userSettingsPath, overwrite: false);
            }
        }

        var identityPath = Path.Combine(configDir, "instance.json");
        if (!File.Exists(identityPath))
        {
            var legacyIdentityPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FieldKb",
                "instance.json");
            if (File.Exists(legacyIdentityPath))
            {
                File.Copy(legacyIdentityPath, identityPath, overwrite: false);
            }
        }

        var dataDir = AppDataPaths.GetAppDataDirectory();
        Directory.CreateDirectory(dataDir);
        var dbPath = AppDataPaths.GetDatabasePath();
        if (!File.Exists(dbPath))
        {
            var legacyDbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FieldKb",
                "kb.sqlite");
            if (File.Exists(legacyDbPath))
            {
                File.Copy(legacyDbPath, dbPath, overwrite: false);
            }
        }

        var attachmentsDir = AppDataPaths.GetAttachmentsDirectory();
        if (!Directory.Exists(attachmentsDir))
        {
            var legacyAttachmentsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FieldKb",
                "attachments");
            if (Directory.Exists(legacyAttachmentsDir))
            {
                CopyDirectory(legacyAttachmentsDir, attachmentsDir);
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: false);
        }
    }

    private static bool EnsureSingleInstance()
    {
        const string mutexName = @"Local\DebugSummaryPlatform_FieldKb";
        const string activateEventName = @"Local\DebugSummaryPlatform_FieldKb_Activate";

        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out var createdNew);
            if (createdNew)
            {
                TryAppendBootstrapLog("单例检查：已获取互斥锁。");
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activateEventName);
                _singleInstanceCts = new CancellationTokenSource();
                _ = Task.Run(() => ActivationLoopAsync(_singleInstanceCts.Token), CancellationToken.None);
                return true;
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        catch
        {
        }

        TryAppendBootstrapLog("单例检查：检测到已有实例，发送激活信号。");
        TrySignalActivate(activateEventName);
        return false;
    }

    private static void TrySignalActivate(string activateEventName)
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(activateEventName);
            ev.Set();
            TryAppendBootstrapLog("单例激活：已发送激活信号。");
        }
        catch
        {
            TryAppendBootstrapLog("单例激活：发送激活信号失败（可能无窗口或权限问题）。");
        }
    }

    private static async Task ActivationLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_activateEvent is null)
                {
                    return;
                }

                if (!_activateEvent.WaitOne(TimeSpan.FromSeconds(1)))
                {
                    continue;
                }

                await Current.Dispatcher.InvokeAsync(() =>
                {
                    Window? window = Current.MainWindow;
                    if (window is null)
                    {
                        foreach (Window w in Current.Windows)
                        {
                            window = w;
                            break;
                        }
                    }
                    if (window is null)
                    {
                        Interlocked.Exchange(ref _pendingActivate, 1);
                        return;
                    }

                    TryActivateWindow(window);
                });
            }
            catch
            {
            }
        }
    }

    private static void TryActivateWindow(Window window)
    {
        if (System.Windows.Application.Current?.Dispatcher.HasShutdownStarted == true)
        {
            return;
        }
        if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }
        catch
        {
        }
    }

    private async Task StartUiWatchdogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                var w = MainWindow ?? _bootstrapWindow;
                if (w is not null)
                {
                    TryActivateWindow(w);
                    return;
                }

                Interlocked.Exchange(ref _pendingActivate, 1);
            });

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var mainVisible = false;
            var bootstrapVisible = false;
            try
            {
                mainVisible = await Dispatcher.InvokeAsync(() => MainWindow?.IsVisible == true);
                bootstrapVisible = await Dispatcher.InvokeAsync(() => _bootstrapWindow?.IsVisible == true);
            }
            catch
            {
            }

            if (mainVisible)
            {
                return;
            }

            if (!bootstrapVisible)
            {
                TryAppendBootstrapLog("窗口自检失败：超过阈值仍无可见窗口，进程将退出。");
                Environment.Exit(2);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken);
            try
            {
                mainVisible = await Dispatcher.InvokeAsync(() => MainWindow?.IsVisible == true);
            }
            catch
            {
            }

            if (!mainVisible)
            {
                TryAppendBootstrapLog("启动超时：启动窗口可见，但主界面长期未显示，进程将退出。");
                try
                {
                    MessageBox.Show("启动超时：已显示启动窗口，但主界面长时间未出现。\n\n程序将自动退出，请查看 data\\logs 或 %TEMP%\\FieldKb_bootstrap.log。", "调试资料汇总平台");
                }
                catch
                {
                }
                Environment.Exit(2);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private static void TryAppendBootstrapLog(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "FieldKb_bootstrap.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
