using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FieldKb.Application.Abstractions;

namespace FieldKb.Client.Wpf;

public sealed partial class BulkExportViewModel : ObservableObject
{
    private readonly IKbStore _store;
    private readonly IBulkExportService _exportService;
    private readonly IUiDialogService _dialogService;

    public BulkExportViewModel(IKbStore store, IBulkExportService exportService, IUiDialogService dialogService)
    {
        _store = store;
        _exportService = exportService;
        _dialogService = dialogService;

        ProfessionOptions = BuildProfessionOptions();
        Tags = new ObservableCollection<TagRow>();
        FormatOptions = new[]
        {
            new FormatOption(BulkExportFormat.Xlsx, "Excel（.xlsx）"),
            new FormatOption(BulkExportFormat.XlsxBundle, "Excel + 附件包（.zip）")
        };
        SelectedFormat = BulkExportFormat.Xlsx;

        LoadCommand = new AsyncRelayCommand(LoadAsync);
        RefreshCountCommand = new AsyncRelayCommand(RefreshCountAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, false));

        StatusText = "加载中…";
    }

    public event EventHandler<bool>? RequestClose;

    public IReadOnlyList<ProfessionFilterOption> ProfessionOptions { get; }
    public ObservableCollection<TagRow> Tags { get; }
    public IReadOnlyList<FormatOption> FormatOptions { get; }

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand RefreshCountCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IRelayCommand CloseCommand { get; }

    [ObservableProperty]
    private string _selectedProfessionFilterId = "all";

    [ObservableProperty]
    private DateTime? _updatedFromDate;

    [ObservableProperty]
    private DateTime? _updatedToDate;

    [ObservableProperty]
    private bool _includeSoftDeleted;

    [ObservableProperty]
    private BulkExportFormat _selectedFormat;

    [ObservableProperty]
    private int _previewCount;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    private async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            StatusText = "加载标签中…";
            var tags = await DbBusyUiRetry.RunAsync(ct => _store.GetAllTagsAsync(ct), actionName: "加载标签", ct: CancellationToken.None);
            Tags.Clear();
            foreach (var t in tags)
            {
                Tags.Add(new TagRow(t.Id, t.Name));
            }

            await RefreshCountAsync();
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

    private async Task RefreshCountAsync()
    {
        try
        {
            IsBusy = true;
            var filter = BuildFilter();
            PreviewCount = await DbBusyUiRetry.RunAsync(ct => _store.CountProblemsForHardDeleteAsync(filter, ct), actionName: "预览数量", ct: CancellationToken.None);
            StatusText = $"符合条件的问题数量：{PreviewCount}";
        }
        catch (Exception ex)
        {
            StatusText = $"预览失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportAsync()
    {
        if (PreviewCount <= 0)
        {
            StatusText = "当前筛选条件下没有可导出的数据。";
            return;
        }

        var filter = BuildFilter();
        var (dialogFilter, ext) = GetDialogFilter(SelectedFormat);
        var fileName = $"FieldKb_export_{DateTimeOffset.Now:yyyyMMdd_HHmmss}{ext}";
        var path = await _dialogService.PickSaveFilePathAsync(dialogFilter, fileName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "导出中…";
            await DbBusyUiRetry.RunAsync(
                ct => _exportService.BulkExportAsync(new BulkExportRequest(path, SelectedFormat, filter), ct),
                actionName: "批量导出",
                ct: CancellationToken.None);
            StatusText = $"导出完成：{path}";
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private ProblemHardDeleteFilter BuildFilter()
    {
        var tags = Tags.Where(t => t.IsSelected).Select(t => t.TagId).ToArray();
        DateTimeOffset? fromUtc = UpdatedFromDate is null
            ? null
            : new DateTimeOffset(UpdatedFromDate.Value.Date, TimeZoneInfo.Local.GetUtcOffset(UpdatedFromDate.Value.Date)).ToUniversalTime();
        DateTimeOffset? toUtc = UpdatedToDate is null
            ? null
            : new DateTimeOffset(UpdatedToDate.Value.Date.AddDays(1).AddTicks(-1), TimeZoneInfo.Local.GetUtcOffset(UpdatedToDate.Value.Date)).ToUniversalTime();

        return new ProblemHardDeleteFilter(tags, SelectedProfessionFilterId, fromUtc, toUtc, IncludeSoftDeleted);
    }

    private static (string dialogFilter, string ext) GetDialogFilter(BulkExportFormat format)
    {
        return format switch
        {
            BulkExportFormat.Xlsx => ("Excel Workbook (*.xlsx)|*.xlsx|All Files (*.*)|*.*", ".xlsx"),
            BulkExportFormat.XlsxBundle => ("Export Bundle (*.zip)|*.zip|All Files (*.*)|*.*", ".zip"),
            _ => ("All Files (*.*)|*.*", string.Empty)
        };
    }

    private static IReadOnlyList<ProfessionFilterOption> BuildProfessionOptions()
    {
        var list = new List<ProfessionFilterOption>
        {
            new("all", "全部"),
            new("unassigned", "未标记")
        };
        list.AddRange(ProfessionIds.Options.Select(p => new ProfessionFilterOption(p.Id, p.DisplayName)));
        return list;
    }

    public sealed record ProfessionFilterOption(string Id, string DisplayName);

    public sealed record FormatOption(BulkExportFormat Format, string DisplayName);

    public sealed partial class TagRow : ObservableObject
    {
        public TagRow(string tagId, string name)
        {
            _tagId = tagId;
            _name = name;
        }

        [ObservableProperty]
        private string _tagId;

        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private bool _isSelected;
    }
}
