using System.IO;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        MigrateConfigIfNeeded();

        var logDir = AppDataPaths.GetLogsDirectory();
        Directory.CreateDirectory(logDir);
        var sessionLogPath = Path.Combine(logDir, $"FieldKb_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.log");

        _host = Host.CreateDefaultBuilder()
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

        _host.Start();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("日志文件：{Path}", sessionLogPath);
        logger.LogInformation("数据库文件：{Path}", _host.Services.GetRequiredService<SqliteOptions>().DatabasePath);
        logger.LogInformation("附件目录：{Path}", AppDataPaths.GetAttachmentsDirectory());
        logger.LogInformation("配置目录：{Path}", AppDataPaths.GetConfigDirectory());

        DispatcherUnhandledException += (_, args) =>
        {
            logger.LogError(args.Exception, "发生未处理 UI 异常。");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                logger.LogCritical(ex, "发生未处理异常，程序即将退出。");
            }
        };

        _ = InitializeAndShowAsync(logger);
    }

    private async Task InitializeAndShowAsync(ILogger logger)
    {
        try
        {
            logger.LogInformation("应用启动：初始化开始。");

            var store = _host!.Services.GetRequiredService<IKbStore>();
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            await _host!.Services.GetRequiredService<IProfessionFixedFieldSettings>()
                .InitializeAsync(CancellationToken.None)
                .ConfigureAwait(false);

            await _host!.Services.GetRequiredService<OperationPasswordService>()
                .InitializeAsync(CancellationToken.None)
                .ConfigureAwait(false);

            await Dispatcher.InvokeAsync(() =>
            {
                var mainWindow = _host!.Services.GetRequiredService<MainWindow>();
                MainWindow = mainWindow;
                mainWindow.Show();
            });

            logger.LogInformation("应用启动：初始化完成。");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "应用初始化失败，程序即将退出。");
            await Dispatcher.InvokeAsync(Shutdown);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.Dispose();
            _host = null;
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
}
