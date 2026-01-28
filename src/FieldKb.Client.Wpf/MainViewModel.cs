using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FieldKb.Application.Abstractions;
using FieldKb.Application.ImportExport;
using FieldKb.Domain.Models;
using FieldKb.Infrastructure.SpreadsheetImport;
using FieldKb.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FieldKb.Client.Wpf;

public partial class MainViewModel : ObservableObject
{
    private const int PageSize = 100;
    private const string ProfessionFilterAll = "all";
    private const string ProfessionFilterUnassigned = "unassigned";
    private readonly IKbStore _store;
    private readonly IPackageTransferService _packageTransferService;
    private readonly IBulkExportService _bulkExportService;
    private readonly ISpreadsheetImportService _spreadsheetImportService;
    private readonly InstanceIdentityProvider _identityProvider;
    private readonly LocalInstanceContext _localInstanceContext;
    private readonly LanExchangeApiHost _lanExchangeApiHost;
    private readonly IUiDialogService _dialogService;
    private readonly IUserContext _userContext;
    private readonly IAppSettingsStore _appSettingsStore;
    private readonly IAppLogStore _logStore;
    private readonly ILogger<MainViewModel> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _statusTransientCts;

    public MainViewModel(
        IKbStore store,
        IPackageTransferService packageTransferService,
        IBulkExportService bulkExportService,
        ISpreadsheetImportService spreadsheetImportService,
        InstanceIdentityProvider identityProvider,
        LocalInstanceContext localInstanceContext,
        LanExchangeApiHost lanExchangeApiHost,
        IUiDialogService dialogService,
        IConfiguration configuration,
        IUserContext userContext,
        IAppSettingsStore appSettingsStore,
        IAppLogStore logStore,
        ILogger<MainViewModel> logger,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _packageTransferService = packageTransferService;
        _bulkExportService = bulkExportService;
        _spreadsheetImportService = spreadsheetImportService;
        _identityProvider = identityProvider;
        _localInstanceContext = localInstanceContext;
        _lanExchangeApiHost = lanExchangeApiHost;
        _dialogService = dialogService;
        _userContext = userContext;
        _appSettingsStore = appSettingsStore;
        _logStore = logStore;
        _logger = logger;
        _loggerFactory = loggerFactory;

        Results = new ObservableCollection<ProblemSearchItem>();
        TagFilters = new ObservableCollection<TagFilterItem>();
        SelectedProblemTags = new ObservableCollection<TagFilterItem>();
        SelectedAttachments = new ObservableCollection<AttachmentItem>();
        ProfessionFilters = new ObservableCollection<ProfessionFilterOption>(BuildProfessionFilters());

        var remoteInstanceId = configuration["Sync:DefaultRemoteInstanceId"];
        RemoteInstanceId = string.IsNullOrWhiteSpace(remoteInstanceId) ? "corporate" : remoteInstanceId;

        NewProblemCommand = new AsyncRelayCommand(NewProblemAsync);
        EditProblemCommand = new AsyncRelayCommand(EditProblemAsync);
        DeleteProblemCommand = new AsyncRelayCommand(DeleteProblemAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        ExportFullCommand = new AsyncRelayCommand(ExportFullAsync);
        ExportIncrementalCommand = new AsyncRelayCommand(ExportIncrementalAsync);
        BulkExportCommand = new AsyncRelayCommand(OpenBulkExportAsync);
        SpreadsheetImportCommand = new AsyncRelayCommand(OpenSpreadsheetImportAsync);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
        OpenTagManagerCommand = new AsyncRelayCommand(OpenTagManagerAsync);
        OpenLanExchangeCommand = new AsyncRelayCommand(OpenLanExchangeAsync);
        AddAttachmentsCommand = new AsyncRelayCommand(AddAttachmentsAsync);
        OpenConflictsCommand = new AsyncRelayCommand(OpenConflictsAsync);
        OpenAttachmentCommand = new RelayCommand<AttachmentItem?>(OpenAttachment);
        PreviewAttachmentCommand = new RelayCommand<AttachmentItem?>(PreviewAttachment);
        ClearTagFiltersCommand = new RelayCommand(ClearTagFilters);
        OpenLogsCommand = new RelayCommand(OpenLogs);
        OpenAboutCommand = new RelayCommand(OpenAbout);
        PrevPageCommand = new RelayCommand(PrevPage, CanPrevPage);
        NextPageCommand = new RelayCommand(NextPage, CanNextPage);

        CurrentUserName = _userContext.CurrentUserName;
        _userContext.CurrentUserNameChanged += (_, name) =>
        {
            _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => CurrentUserName = name);
        };

        CurrentProfessionDisplayName = GetProfessionDisplayName(_userContext.CurrentProfessionId);
        _userContext.CurrentProfessionIdChanged += (_, professionId) =>
        {
            _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => CurrentProfessionDisplayName = GetProfessionDisplayName(professionId));
        };
    }

    [ObservableProperty]
    private string _title = "调试资料汇总平台";

    [ObservableProperty]
    private string _subtitle = "离线原型";

    [ObservableProperty]
    private string _queryText = string.Empty;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private int _totalResultCount;

    [ObservableProperty]
    private bool _hasNextPage;

    [ObservableProperty]
    private string _localInstanceId = string.Empty;

    [ObservableProperty]
    private string _currentUserName = string.Empty;

    [ObservableProperty]
    private string _currentProfessionDisplayName = string.Empty;

    [ObservableProperty]
    private string _remoteInstanceId = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _selectedProfessionFilterId = ProfessionFilterAll;

    [ObservableProperty]
    private ProblemSearchItem? _selectedResult;

    [ObservableProperty]
    private Problem? _selectedProblem;

    [ObservableProperty]
    private string _selectedEnvironmentText = string.Empty;

    [ObservableProperty]
    private string _selectedTagsText = string.Empty;

    public ObservableCollection<ProblemSearchItem> Results { get; }
    public ObservableCollection<TagFilterItem> TagFilters { get; }
    public ObservableCollection<TagFilterItem> SelectedProblemTags { get; }
    public ObservableCollection<AttachmentItem> SelectedAttachments { get; }
    public ObservableCollection<ProfessionFilterOption> ProfessionFilters { get; }

    [RelayCommand]
    private async Task CopyLocalInstanceIdAsync()
    {
        if (string.IsNullOrWhiteSpace(LocalInstanceId))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(LocalInstanceId);
            await SetTransientStatusAsync("已复制本机实例ID。", TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败：{ex.Message}";
        }
    }

    private async Task SetTransientStatusAsync(string message, TimeSpan ttl)
    {
        _statusTransientCts?.Cancel();
        _statusTransientCts = new CancellationTokenSource();
        var ct = _statusTransientCts.Token;

        StatusText = message;

        try
        {
            await Task.Delay(ttl, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!ct.IsCancellationRequested && string.Equals(StatusText, message, StringComparison.Ordinal))
        {
            StatusText = string.Empty;
        }
    }

    public IAsyncRelayCommand NewProblemCommand { get; }
    public IAsyncRelayCommand EditProblemCommand { get; }
    public IAsyncRelayCommand DeleteProblemCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }
    public IAsyncRelayCommand ExportFullCommand { get; }
    public IAsyncRelayCommand ExportIncrementalCommand { get; }
    public IAsyncRelayCommand BulkExportCommand { get; }
    public IAsyncRelayCommand SpreadsheetImportCommand { get; }
    public IAsyncRelayCommand OpenSettingsCommand { get; }
    public IAsyncRelayCommand OpenTagManagerCommand { get; }
    public IAsyncRelayCommand OpenLanExchangeCommand { get; }
    public IAsyncRelayCommand AddAttachmentsCommand { get; }
    public IAsyncRelayCommand OpenConflictsCommand { get; }
    public IRelayCommand<AttachmentItem?> OpenAttachmentCommand { get; }
    public IRelayCommand<AttachmentItem?> PreviewAttachmentCommand { get; }
    public IRelayCommand ClearTagFiltersCommand { get; }
    public IRelayCommand OpenLogsCommand { get; }
    public IRelayCommand OpenAboutCommand { get; }
    public IRelayCommand PrevPageCommand { get; }
    public IRelayCommand NextPageCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var identity = await _identityProvider.GetOrCreateAsync(_localInstanceContext.Kind, cancellationToken);
        LocalInstanceId = identity.InstanceId;
        Subtitle = $"{identity.Kind} / {identity.InstanceId}";

        await RefreshTagsAsync(cancellationToken);
        ResetAndSearch();
        _logger.LogInformation("主界面初始化完成。InstanceId={InstanceId}", LocalInstanceId);
    }

    partial void OnQueryTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        CurrentPage = 1;
        TotalResultCount = 0;
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        _ = LoadPageAsync(value, CurrentPage, _searchCts.Token);
    }

    partial void OnSelectedResultChanged(ProblemSearchItem? value)
    {
        _ = LoadSelectedAsync(value);
    }

    partial void OnSelectedProfessionFilterIdChanged(string value)
    {
        ResetAndSearch();
    }

    private async Task LoadPageAsync(string query, int page, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken);

            var selectedTags = GetSelectedTagIds();
            var professionFilterId = SelectedProfessionFilterId;
            if (page < 1)
            {
                page = 1;
            }

            IsBusy = true;

            var result = await Task.Run(async () =>
            {
                var offset = (page - 1) * PageSize;
                var hits = await DbBusyUiRetry.RunAsync(
                    ct => _store.SearchProblemsAsync(query, selectedTags, professionFilterId, limit: PageSize + 1, offset: offset, ct),
                    actionName: "查询数据",
                    ct: cancellationToken);
                var total = await DbBusyUiRetry.RunAsync(
                    ct => _store.CountProblemsAsync(query, selectedTags, professionFilterId, ct),
                    actionName: "统计数量",
                    ct: cancellationToken);
                var hasNext = hits.Count > PageSize;
                var trimmed = hits.Take(PageSize).ToArray();

                var list = new List<ProblemSearchItem>(trimmed.Length);
                foreach (var hit in trimmed)
                {
                    list.Add(new ProblemSearchItem(hit.ProblemId, hit.Title, hit.Snippet, hit.Score));
                }

                return (list, hasNext, total);
            }, cancellationToken);

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Results.Clear();
                foreach (var item in result.list)
                {
                    Results.Add(item);
                }

                HasNextPage = result.hasNext;
                TotalResultCount = result.total;
                CurrentPage = page;
                PrevPageCommand.NotifyCanExecuteChanged();
                NextPageCommand.NotifyCanExecuteChanged();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = $"搜索失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public string PageInfoText => TotalResultCount <= 0 ? $"第 {CurrentPage} 页" : $"第 {CurrentPage} 页（共 {TotalResultCount} 条）";

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(PageInfoText));
    }

    partial void OnTotalResultCountChanged(int value)
    {
        OnPropertyChanged(nameof(PageInfoText));
    }

    private static IReadOnlyList<ProfessionFilterOption> BuildProfessionFilters()
    {
        var list = new List<ProfessionFilterOption>
        {
            new ProfessionFilterOption(ProfessionFilterAll, "全部"),
            new ProfessionFilterOption(ProfessionFilterUnassigned, "未标记")
        };

        foreach (var opt in ProfessionIds.Options)
        {
            list.Add(new ProfessionFilterOption(opt.Id, opt.DisplayName));
        }

        return list;
    }

    private static string GetProfessionDisplayName(string? professionId)
    {
        var id = ProfessionIds.Normalize(professionId);
        return ProfessionIds.Options.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal))?.DisplayName ?? id;
    }


    private async Task LoadSelectedAsync(ProblemSearchItem? selected)
    {
        if (selected is null)
        {
            SelectedProblem = null;
            SelectedEnvironmentText = string.Empty;
            SelectedTagsText = string.Empty;
            SelectedProblemTags.Clear();
            SelectedAttachments.Clear();
            return;
        }

        try
        {
            IsBusy = true;
            var snapshot = await Task.Run(async () =>
            {
                var p = await DbBusyUiRetry.RunAsync(ct => _store.GetProblemByIdAsync(selected.ProblemId, ct), actionName: "加载详情", ct: CancellationToken.None);
                if (p is null)
                {
                    return (problem: (Problem?)null, envText: string.Empty, tags: Array.Empty<Tag>(), attachments: Array.Empty<Attachment>());
                }

                var envText = EnvironmentJson.ToPrettyText(p.EnvironmentJson);
                var tags = await DbBusyUiRetry.RunAsync(ct => _store.GetTagsForProblemAsync(p.Id, ct), actionName: "加载标签", ct: CancellationToken.None);
                var attachments = await DbBusyUiRetry.RunAsync(ct => _store.GetAttachmentsForProblemAsync(p.Id, ct), actionName: "加载附件", ct: CancellationToken.None);
                return (problem: (Problem?)p, envText, tags, attachments);
            }, CancellationToken.None);

            SelectedProblem = snapshot.problem;
            SelectedEnvironmentText = snapshot.envText;

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SelectedProblemTags.Clear();
                foreach (var t in snapshot.tags)
                {
                    SelectedProblemTags.Add(new TagFilterItem(t.Id, t.Name, isSelected: false, onChanged: null));
                }

                SelectedTagsText = snapshot.tags.Count == 0 ? string.Empty : string.Join(" / ", snapshot.tags.Select(t => t.Name));

                SelectedAttachments.Clear();
                foreach (var a in snapshot.attachments)
                {
                    SelectedAttachments.Add(new AttachmentItem(a.Id, a.OriginalFileName, a.ContentHash, a.SizeBytes));
                }
            });
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task NewProblemAsync()
    {
        var now = DateTimeOffset.UtcNow;

        var tagOptions = await DbBusyUiRetry.RunAsync(
            ct => BuildTagOptionsAsync(selectedTagIds: Array.Empty<string>(), ct),
            actionName: "加载标签",
            ct: CancellationToken.None);
        var editorData = await _dialogService.ShowProblemEditorAsync(new ProblemEditorData(
            Title: string.Empty,
            Symptom: string.Empty,
            RootCause: string.Empty,
            Solution: string.Empty,
            EnvironmentJson: "{}",
            Tags: tagOptions));

        if (editorData is null)
        {
            return;
        }

        var createdBy = string.IsNullOrWhiteSpace(CurrentUserName) ? Environment.UserName : CurrentUserName;
        var updatedBy = string.IsNullOrWhiteSpace(LocalInstanceId) ? "unknown" : LocalInstanceId;
        var sourceKind = _localInstanceContext.Kind == InstanceKind.Corporate ? SourceKind.Corporate : SourceKind.Personal;
        var professionId = ProfessionIds.Normalize(_userContext.CurrentProfessionId);
        var envJson = EnvironmentJson.SetOrReplaceMeta(editorData.EnvironmentJson, EnvironmentJson.KnownKeys.ProfessionId, professionId);

        var problem = new Problem(
            Id: Guid.NewGuid().ToString("D"),
            Title: editorData.Title,
            Symptom: editorData.Symptom,
            RootCause: editorData.RootCause,
            Solution: editorData.Solution,
            EnvironmentJson: envJson,
            Severity: 0,
            Status: 0,
            CreatedAtUtc: now,
            CreatedBy: createdBy,
            UpdatedAtUtc: now,
            UpdatedByInstanceId: updatedBy,
            IsDeleted: false,
            DeletedAtUtc: null,
            SourceKind: sourceKind);

        IsBusy = true;
        try
        {
            await DbBusyUiRetry.RunAsync(async ct =>
            {
                await _store.UpsertProblemAsync(problem, ct);
                var selectedTags = editorData.Tags.Where(t => t.IsSelected).Select(t => t.TagId).ToArray();
                await _store.SetTagsForProblemAsync(problem.Id, selectedTags, now, updatedBy, ct);
            }, actionName: "保存问题", ct: CancellationToken.None);
            StatusText = "已保存问题记录。";
            ResetAndSearch();
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EditProblemAsync()
    {
        if (SelectedProblem is null)
        {
            return;
        }

        _logger.LogInformation("编辑问题：{ProblemId}", SelectedProblem.Id);
        var now = DateTimeOffset.UtcNow;
        var updatedBy = string.IsNullOrWhiteSpace(LocalInstanceId) ? "unknown" : LocalInstanceId;

        var selectedTagIds = await DbBusyUiRetry.RunAsync(async ct =>
        {
            var selectedTags = await _store.GetTagsForProblemAsync(SelectedProblem.Id, ct);
            return selectedTags.Select(t => t.Id).ToArray();
        }, actionName: "加载标签", ct: CancellationToken.None);
        var tagOptions = await DbBusyUiRetry.RunAsync(ct => BuildTagOptionsAsync(selectedTagIds, ct), actionName: "加载标签", ct: CancellationToken.None);

        var editorData = await _dialogService.ShowProblemEditorAsync(new ProblemEditorData(
            Title: SelectedProblem.Title,
            Symptom: SelectedProblem.Symptom,
            RootCause: SelectedProblem.RootCause,
            Solution: SelectedProblem.Solution,
            EnvironmentJson: SelectedProblem.EnvironmentJson,
            Tags: tagOptions));

        if (editorData is null)
        {
            return;
        }

        var existingProfessionId = EnvironmentJson.TryGetValue(SelectedProblem.EnvironmentJson, EnvironmentJson.KnownKeys.ProfessionId);
        var professionId = ProfessionIds.Normalize(existingProfessionId ?? _userContext.CurrentProfessionId);
        var envJson = EnvironmentJson.SetOrReplaceMeta(editorData.EnvironmentJson, EnvironmentJson.KnownKeys.ProfessionId, professionId);

        var updatedProblem = SelectedProblem with
        {
            Title = editorData.Title,
            Symptom = editorData.Symptom,
            RootCause = editorData.RootCause,
            Solution = editorData.Solution,
            EnvironmentJson = envJson,
            UpdatedAtUtc = now,
            UpdatedByInstanceId = updatedBy
        };

        IsBusy = true;
        try
        {
            await DbBusyUiRetry.RunAsync(async ct =>
            {
                await _store.UpsertProblemAsync(updatedProblem, ct);
                var tagIds = editorData.Tags.Where(t => t.IsSelected).Select(t => t.TagId).ToArray();
                await _store.SetTagsForProblemAsync(updatedProblem.Id, tagIds, now, updatedBy, ct);
            }, actionName: "更新问题", ct: CancellationToken.None);
            StatusText = "已更新问题记录。";
            ResetAndSearch();
        }
        catch (Exception ex)
        {
            StatusText = $"更新失败：{ex.Message}";
            _logger.LogError(ex, "更新问题失败：{ProblemId}", SelectedProblem.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteProblemAsync()
    {
        if (SelectedProblem is null)
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            "确定要删除该问题记录吗？（软删除，可通过离线包导入回溯）",
            "确认删除",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.OK)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var updatedBy = string.IsNullOrWhiteSpace(LocalInstanceId) ? "unknown" : LocalInstanceId;

        IsBusy = true;
        try
        {
            await DbBusyUiRetry.RunAsync(ct => _store.SoftDeleteProblemAsync(SelectedProblem.Id, now, updatedBy, ct), actionName: "删除问题", ct: CancellationToken.None);
            StatusText = "已删除（软删除）问题记录。";
            ResetAndSearch();
        }
        catch (Exception ex)
        {
            StatusText = $"删除失败：{ex.Message}";
            _logger.LogError(ex, "软删除问题失败：{ProblemId}", SelectedProblem.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportAsync()
    {
        var path = await _dialogService.PickImportPackagePathAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        try
        {
            _logger.LogInformation("开始导入离线包：{Path}", path);
            var report = await Task.Run(async () => await _packageTransferService.ImportAsync(path, CancellationToken.None), CancellationToken.None);
            var importedProblems = report.ProblemsImportedCount ?? report.ImportedCount;
            var skippedProblems = report.ProblemsSkippedCount ?? report.SkippedCount;
            var conflictProblems = report.ProblemsConflictCount ?? report.ConflictCount;
            StatusText = $"导入完成：问题 {importedProblems}，跳过 {skippedProblems}，冲突 {conflictProblems}。";
            _logger.LogInformation(
                "导入完成：问题导入={ProblemsImported} 标签导入={TagsImported} 关联导入={ProblemTagsImported} 附件导入={AttachmentsImported} 总导入={Imported} 跳过={Skipped} 冲突={Conflicts}",
                report.ProblemsImportedCount ?? 0,
                report.TagsImportedCount ?? 0,
                report.ProblemTagsImportedCount ?? 0,
                report.AttachmentsImportedCount ?? 0,
                report.ImportedCount,
                report.SkippedCount,
                report.ConflictCount);
            ResetAndSearch();
        }
        catch (Exception ex)
        {
            StatusText = $"导入失败：{ex.Message}";
            _logger.LogError(ex, "导入失败：{Path}", path);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenSettingsAsync()
    {
        try
        {
            _logger.LogInformation("打开设置窗口。");
            var result = await _dialogService.ShowUserSettingsAsync(CurrentUserName, _userContext.CurrentProfessionId);
            if (result is null)
            {
                return;
            }

            if (!UserNameRules.IsValid(result.UserName, out var error))
            {
                StatusText = error ?? "用户名不合法。";
                return;
            }

            _userContext.SetCurrentUserName(result.UserName);
            _userContext.SetCurrentProfessionId(result.ProfessionId);
            await Task.Run(async () =>
            {
                await _appSettingsStore.WriteUserNameAsync(result.UserName, CancellationToken.None);
                await _appSettingsStore.WriteProfessionIdAsync(result.ProfessionId, CancellationToken.None);
            }, CancellationToken.None);
            StatusText = "已更新设置。";
            _logger.LogInformation("已更新设置：UserName={UserName} ProfessionId={ProfessionId}", result.UserName, result.ProfessionId);
        }
        catch (Exception ex)
        {
            StatusText = $"设置失败：{ex.Message}";
            _logger.LogError(ex, "设置保存失败。");
        }
    }

    private async Task OpenTagManagerAsync()
    {
        _logger.LogInformation("打开标签管理窗口。");
        var window = new TagManagerWindow
        {
            Owner = System.Windows.Application.Current.MainWindow,
            DataContext = new TagManagerViewModel(_store, string.IsNullOrWhiteSpace(LocalInstanceId) ? "unknown" : LocalInstanceId)
        };

        window.ShowDialog();
        await RefreshTagsAsync(CancellationToken.None);
        ResetAndSearch();
    }

    private Task OpenLanExchangeAsync()
    {
        _logger.LogInformation("打开局域网交换窗口。");
        var window = new LanExchangeWindow
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        void OnImported()
        {
            _ = RefreshTagsAsync(CancellationToken.None);
            ResetAndSearch();
        }

        window.DataContext = new LanExchangeViewModel(
            _lanExchangeApiHost,
            _localInstanceContext,
            _packageTransferService,
            _appSettingsStore,
            _loggerFactory.CreateLogger<LanExchangeViewModel>(),
            localInstanceId: LocalInstanceId,
            initialRemoteInstanceId: RemoteInstanceId,
            close: () => window.Close(),
            onImported: OnImported);

        window.ShowDialog();
        return Task.CompletedTask;
    }

    private async Task AddAttachmentsAsync()
    {
        if (SelectedProblem is null)
        {
            return;
        }

        var files = await _dialogService.PickAttachmentFilePathsAsync();
        if (files is null || files.Length == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var updatedBy = string.IsNullOrWhiteSpace(LocalInstanceId) ? "unknown" : LocalInstanceId;

        IsBusy = true;
        try
        {
            _logger.LogInformation("添加附件：ProblemId={ProblemId} Count={Count}", SelectedProblem.Id, files.Length);
            await Task.Run(async () =>
            {
                foreach (var file in files)
                {
                    await _store.AddAttachmentAsync(SelectedProblem.Id, file, now, updatedBy, CancellationToken.None);
                }
            }, CancellationToken.None);

            await LoadSelectedAsync(SelectedResult);
            StatusText = "已添加附件。";
            _logger.LogInformation("添加附件完成：ProblemId={ProblemId}", SelectedProblem.Id);
        }
        catch (Exception ex)
        {
            StatusText = $"添加附件失败：{ex.Message}";
            _logger.LogError(ex, "添加附件失败：ProblemId={ProblemId}", SelectedProblem.Id);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void OpenAttachment(AttachmentItem? item)
    {
        if (item is null)
        {
            return;
        }

        _ = OpenAttachmentAsync(item);
    }

    public void PreviewAttachment(AttachmentItem? item)
    {
        if (item is null)
        {
            return;
        }

        _ = PreviewAttachmentAsync(item);
    }

    private async Task OpenAttachmentAsync(AttachmentItem item)
    {
        var path = await _store.GetAttachmentLocalPathAsync(item.ContentHash, CancellationToken.None);
        if (!File.Exists(path))
        {
            StatusText = "附件文件不存在。";
            _logger.LogWarning("打开附件失败：文件不存在。Hash={Hash} Name={Name}", item.ContentHash, item.FileName);
            return;
        }

        _logger.LogInformation("打开附件：{Path}", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async Task PreviewAttachmentAsync(AttachmentItem item)
    {
        var path = await _store.GetAttachmentLocalPathAsync(item.ContentHash, CancellationToken.None);
        if (!File.Exists(path))
        {
            StatusText = "附件文件不存在。";
            _logger.LogWarning("预览附件失败：文件不存在。Hash={Hash} Name={Name}", item.ContentHash, item.FileName);
            return;
        }

        if (!AttachmentPreviewViewModel.CanPreview(item.FileName))
        {
            _logger.LogInformation("预览附件：不支持内置预览，改为外部打开。Path={Path}", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }

        _logger.LogInformation("预览附件：打开内置预览。Path={Path}", path);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var window = new AttachmentPreviewWindow
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            window.DataContext = new AttachmentPreviewViewModel(path, item.FileName, close: () => window.Close());
            window.ShowDialog();
        });
    }

    private void ClearTagFilters()
    {
        foreach (var tag in TagFilters)
        {
            tag.IsSelected = false;
        }
        ResetAndSearch();
    }

    private Task OpenConflictsAsync()
    {
        _logger.LogInformation("打开冲突中心窗口。");
        var window = new ConflictCenterWindow
        {
            Owner = System.Windows.Application.Current.MainWindow,
            DataContext = new ConflictCenterViewModel(_store, string.IsNullOrWhiteSpace(LocalInstanceId) ? "unknown" : LocalInstanceId)
        };

        window.ShowDialog();
        return Task.CompletedTask;
    }

    private void OpenLogs()
    {
        _logger.LogInformation("打开日志窗口。");
        var window = new LogWindow
        {
            Owner = System.Windows.Application.Current.MainWindow,
            DataContext = new LogViewModel(_logStore)
        };

        window.ShowDialog();
    }

    private void OpenAbout()
    {
        _logger.LogInformation("打开关于窗口。");
        var window = new AboutWindow
        {
            Owner = System.Windows.Application.Current.MainWindow,
            DataContext = new AboutViewModel()
        };
        window.ShowDialog();
    }

    private async Task RefreshTagsAsync(CancellationToken cancellationToken)
    {
        var tags = await DbBusyUiRetry.RunAsync(ct => _store.GetAllTagsAsync(ct), actionName: "刷新标签", ct: cancellationToken);
        var selected = new HashSet<string>(TagFilters.Where(t => t.IsSelected).Select(t => t.TagId), StringComparer.Ordinal);

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            TagFilters.Clear();
            foreach (var tag in tags)
            {
                    var item = new TagFilterItem(tag.Id, tag.Name, selected.Contains(tag.Id), ResetAndSearch);
                TagFilters.Add(item);
            }
        });
    }

    private IReadOnlyList<string> GetSelectedTagIds()
    {
        return TagFilters.Where(t => t.IsSelected).Select(t => t.TagId).ToArray();
    }

    private void TriggerSearch()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = LoadPageAsync(QueryText, CurrentPage, _searchCts.Token);
    }

    private void ResetAndSearch()
    {
        CurrentPage = 1;
        TriggerSearch();
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private void PrevPage()
    {
        if (CurrentPage <= 1)
        {
            return;
        }

        CurrentPage--;
        TriggerSearch();
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private bool CanPrevPage()
    {
        return CurrentPage > 1;
    }

    private void NextPage()
    {
        if (!HasNextPage)
        {
            return;
        }

        CurrentPage++;
        TriggerSearch();
        PrevPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    private bool CanNextPage()
    {
        return HasNextPage;
    }

    private async Task<IReadOnlyList<TagOption>> BuildTagOptionsAsync(IReadOnlyList<string> selectedTagIds, CancellationToken cancellationToken)
    {
        var tags = await _store.GetAllTagsAsync(cancellationToken);
        var selected = new HashSet<string>(selectedTagIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        return tags.Select(t => new TagOption(t.Id, t.Name, selected.Contains(t.Id))).ToArray();
    }

    private Task ExportFullAsync() => ExportAsync(ExportMode.Full);

    private Task ExportIncrementalAsync() => ExportAsync(ExportMode.Incremental);

    private async Task ExportAsync(ExportMode mode)
    {
        var dir = await _dialogService.PickExportDirectoryAsync();
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var request = new ExportRequest(
                OutputDirectory: dir,
                RemoteInstanceId: RemoteInstanceId,
                Mode: mode,
                UpdatedAfterUtc: null,
                Limit: null);
            var result = await Task.Run(async () => await _packageTransferService.ExportAsync(request, CancellationToken.None), CancellationToken.None);

            StatusText = $"导出完成：{result.PackagePath}";
            _logger.LogInformation("导出完成：{Path}", result.PackagePath);
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
            _logger.LogError(ex, "导出失败。Mode={Mode}", mode);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenBulkExportAsync()
    {
        _logger.LogInformation("打开批量导出窗口。");
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var window = new BulkExportWindow
            {
                Owner = System.Windows.Application.Current.MainWindow,
                DataContext = new BulkExportViewModel(_store, _bulkExportService, _dialogService)
            };
            window.ShowDialog();
        });
    }

    private async Task OpenSpreadsheetImportAsync()
    {
        _logger.LogInformation("打开表单导入窗口。");
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var window = new SpreadsheetImportWindow
            {
                Owner = System.Windows.Application.Current.MainWindow,
                DataContext = new SpreadsheetImportViewModel(_spreadsheetImportService, _dialogService)
            };
            window.ShowDialog();
        });
    }

    public sealed record ProblemSearchItem(string ProblemId, string Title, string? Snippet, double Score);

    public sealed record ProfessionFilterOption(string Id, string DisplayName);

    public sealed partial class TagFilterItem : ObservableObject
    {
        private readonly Action? _onChanged;

        public TagFilterItem(string tagId, string name, bool isSelected, Action? onChanged)
        {
            TagId = tagId;
            Name = name;
            _isSelected = isSelected;
            _onChanged = onChanged;
        }

        public string TagId { get; }

        public string Name { get; }

        [ObservableProperty]
        private bool _isSelected;

        partial void OnIsSelectedChanged(bool value)
        {
            _onChanged?.Invoke();
        }
    }

    public sealed record AttachmentItem(string AttachmentId, string FileName, string ContentHash, long SizeBytes);
}
