using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FieldKb.Application.Abstractions;

namespace FieldKb.Client.Wpf;

public sealed partial class DataPurgeViewModel : ObservableObject
{
    private readonly IKbStore _store;
    private readonly OperationPasswordService _passwordService;

    public DataPurgeViewModel(IKbStore store, OperationPasswordService passwordService)
    {
        _store = store;
        _passwordService = passwordService;

        ProfessionOptions = BuildProfessionOptions();
        Tags = new ObservableCollection<TagRow>();

        RefreshCountCommand = new AsyncRelayCommand(RefreshCountAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, false));
        LoadCommand = new AsyncRelayCommand(LoadAsync);
    }

    public event EventHandler<bool>? RequestClose;

    public IReadOnlyList<ProfessionFilterOption> ProfessionOptions { get; }
    public ObservableCollection<TagRow> Tags { get; }

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand RefreshCountCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
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
    private int _previewCount;

    [ObservableProperty]
    private string _password = string.Empty;

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
            await _passwordService.InitializeAsync(CancellationToken.None);
            var tags = await DbBusyUiRetry.RunAsync(ct => _store.GetAllTagsAsync(ct), actionName: "加载标签", ct: CancellationToken.None);
            Tags.Clear();
            foreach (var t in tags)
            {
                Tags.Add(new TagRow(t.Id, t.Name));
            }

            StatusText = _passwordService.IsConfigured
                ? "提示：删除为物理删除，无法恢复。"
                : "未设置操作密码：请先到“设置 → 操作密码”设置后再执行删除。";
            await RefreshCountAsync();
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

    private async Task DeleteAsync()
    {
        await _passwordService.InitializeAsync(CancellationToken.None);
        if (!_passwordService.IsConfigured)
        {
            StatusText = "未设置操作密码，无法执行删除。";
            return;
        }

        if (!_passwordService.Verify(Password ?? string.Empty))
        {
            StatusText = "操作密码不正确。";
            return;
        }

        if (PreviewCount <= 0)
        {
            StatusText = "当前筛选条件下没有可删除的数据。";
            return;
        }

        try
        {
            IsBusy = true;
            var filter = BuildFilter();
            var deleted = await DbBusyUiRetry.RunAsync(ct => _store.HardDeleteProblemsAsync(filter, ct), actionName: "批量删除", ct: CancellationToken.None);
            StatusText = $"删除完成：已物理删除 {deleted} 条问题。";
            Password = string.Empty;
            await RefreshCountAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"删除失败：{ex.Message}";
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
