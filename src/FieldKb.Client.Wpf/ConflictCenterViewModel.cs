using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FieldKb.Application.Abstractions;
using FieldKb.Domain.Models;

namespace FieldKb.Client.Wpf;

public sealed partial class ConflictCenterViewModel : ObservableObject
{
    private readonly IKbStore _store;
    private readonly string _resolvedByInstanceId;

    public ConflictCenterViewModel(IKbStore store, string resolvedByInstanceId)
    {
        _store = store;
        _resolvedByInstanceId = resolvedByInstanceId;
        Conflicts = new ObservableCollection<ConflictItem>();
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        KeepLocalCommand = new AsyncRelayCommand(KeepLocalAsync);
        UseImportedCommand = new AsyncRelayCommand(UseImportedAsync);
        KeepLocalSelectedCommand = new AsyncRelayCommand(() => ResolveSelectedAsync(ConflictResolution.KeepLocal));
        UseImportedSelectedCommand = new AsyncRelayCommand(() => ResolveSelectedAsync(ConflictResolution.UseImported));
        SelectAllCommand = new RelayCommand(SelectAll);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        _ = RefreshAsync();
    }

    public ObservableCollection<ConflictItem> Conflicts { get; }

    [ObservableProperty]
    private ConflictItem? _selectedConflict;

    [ObservableProperty]
    private string _localText = string.Empty;

    [ObservableProperty]
    private string _importedText = string.Empty;

    [ObservableProperty]
    private string _diffText = string.Empty;

    [ObservableProperty]
    private string _localHeaderText = "本地版本";

    [ObservableProperty]
    private string _importedHeaderText = "导入版本";

    [ObservableProperty]
    private string _statusText = string.Empty;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand KeepLocalCommand { get; }
    public IAsyncRelayCommand UseImportedCommand { get; }
    public IAsyncRelayCommand KeepLocalSelectedCommand { get; }
    public IAsyncRelayCommand UseImportedSelectedCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }

    [ObservableProperty]
    private int _selectedCount;

    partial void OnSelectedConflictChanged(ConflictItem? value)
    {
        _ = LoadDetailAsync(value);
    }

    private async Task RefreshAsync()
    {
        try
        {
            var list = await DbBusyUiRetry.RunAsync(ct => _store.GetUnresolvedConflictsAsync(200, ct), actionName: "加载冲突列表", ct: CancellationToken.None);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Conflicts.Clear();
                foreach (var c in list)
                {
                    var item = new ConflictItem(
                        c.ConflictId,
                        c.EntityType,
                        c.EntityId,
                        c.ImportedUpdatedAtUtc,
                        c.LocalUpdatedAtUtc,
                        c.CreatedAtUtc);
                    item.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(ConflictItem.IsSelected))
                        {
                            RecalculateSelection();
                        }
                    };
                    Conflicts.Add(item);
                }
                RecalculateSelection();
            });
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
    }

    private async Task LoadDetailAsync(ConflictItem? item)
    {
        if (item is null)
        {
            LocalText = string.Empty;
            ImportedText = string.Empty;
            DiffText = string.Empty;
            LocalHeaderText = "本地版本";
            ImportedHeaderText = "导入版本";
            return;
        }

        ConflictRecordDetail? detail;
        try
        {
            detail = await DbBusyUiRetry.RunAsync(ct => _store.GetConflictDetailAsync(item.ConflictId, ct), actionName: "加载冲突详情", ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
            return;
        }
        if (detail is null)
        {
            LocalText = string.Empty;
            ImportedText = string.Empty;
            DiffText = string.Empty;
            LocalHeaderText = "本地版本";
            ImportedHeaderText = "导入版本";
            return;
        }

        LocalHeaderText = $"本地版本（更新时间：{detail.LocalUpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}）";
        ImportedHeaderText = $"导入版本（更新时间：{detail.ImportedUpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}）";

        LocalText = ConflictTextFormatter.Format(detail.EntityType, detail.LocalJson);
        ImportedText = ConflictTextFormatter.Format(detail.EntityType, detail.ImportedJson);
        DiffText = ConflictTextFormatter.BuildDiff(detail.EntityType, detail.LocalJson, detail.ImportedJson, detail.LocalUpdatedAtUtc, detail.ImportedUpdatedAtUtc);
    }

    private async Task KeepLocalAsync()
    {
        if (SelectedConflict is null)
        {
            return;
        }

        try
        {
            await DbBusyUiRetry.RunAsync(
                ct => _store.ResolveConflictAsync(SelectedConflict.ConflictId, ConflictResolution.KeepLocal, DateTimeOffset.UtcNow, _resolvedByInstanceId, ct),
                actionName: "处理冲突",
                ct: CancellationToken.None);
            StatusText = "已保留本地版本。";
            await RefreshAsync();
            SelectedConflict = null;
        }
        catch (Exception ex)
        {
            StatusText = $"处理失败：{ex.Message}";
        }
    }

    private async Task UseImportedAsync()
    {
        if (SelectedConflict is null)
        {
            return;
        }

        try
        {
            await DbBusyUiRetry.RunAsync(
                ct => _store.ResolveConflictAsync(SelectedConflict.ConflictId, ConflictResolution.UseImported, DateTimeOffset.UtcNow, _resolvedByInstanceId, ct),
                actionName: "处理冲突",
                ct: CancellationToken.None);
            StatusText = "已采用导入版本。";
            await RefreshAsync();
            SelectedConflict = null;
        }
        catch (Exception ex)
        {
            StatusText = $"处理失败：{ex.Message}";
        }
    }

    private void SelectAll()
    {
        foreach (var c in Conflicts)
        {
            c.IsSelected = true;
        }
        RecalculateSelection();
    }

    private void ClearSelection()
    {
        foreach (var c in Conflicts)
        {
            c.IsSelected = false;
        }
        RecalculateSelection();
    }

    private void RecalculateSelection()
    {
        SelectedCount = Conflicts.Count(c => c.IsSelected);
    }

    private List<ConflictItem> GetSelectedItems()
    {
        var selected = Conflicts.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0 && SelectedConflict is not null)
        {
            selected.Add(SelectedConflict);
        }
        return selected;
    }

    private async Task ResolveSelectedAsync(ConflictResolution resolution)
    {
        var selected = GetSelectedItems();
        if (selected.Count == 0)
        {
            return;
        }

        var ok = 0;
        var failed = 0;
        foreach (var item in selected)
        {
            try
            {
                await DbBusyUiRetry.RunAsync(
                    ct => _store.ResolveConflictAsync(item.ConflictId, resolution, DateTimeOffset.UtcNow, _resolvedByInstanceId, ct),
                    actionName: "批量处理冲突",
                    ct: CancellationToken.None);
                ok++;
            }
            catch
            {
                failed++;
            }
        }

        StatusText = failed == 0
            ? $"已处理 {ok} 条冲突。"
            : $"已处理 {ok} 条冲突，失败 {failed} 条。";

        await RefreshAsync();
        SelectedConflict = null;
        ClearSelection();
    }

    public sealed partial class ConflictItem : ObservableObject
    {
        public ConflictItem(
            string conflictId,
            string entityType,
            string entityId,
            DateTimeOffset importedUpdatedAtUtc,
            DateTimeOffset localUpdatedAtUtc,
            DateTimeOffset createdAtUtc)
        {
            ConflictId = conflictId;
            EntityType = entityType;
            EntityId = entityId;
            ImportedUpdatedAtUtc = importedUpdatedAtUtc;
            LocalUpdatedAtUtc = localUpdatedAtUtc;
            CreatedAtUtc = createdAtUtc;
        }

        public string ConflictId { get; }
        public string EntityType { get; }
        public string EntityId { get; }
        public DateTimeOffset ImportedUpdatedAtUtc { get; }
        public DateTimeOffset LocalUpdatedAtUtc { get; }
        public DateTimeOffset CreatedAtUtc { get; }

        [ObservableProperty]
        private bool _isSelected;

        public string DisplayText => $"{EntityTypeDisplay} / {ShortEntityId}";

        public string SubText => $"{EntityId}  创建：{CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

        private string ShortEntityId => string.IsNullOrWhiteSpace(EntityId) ? string.Empty : (EntityId.Length <= 8 ? EntityId : EntityId[..8]);

        private string EntityTypeDisplay => EntityType switch
        {
            "Problem" => "问题",
            "Tag" => "标签",
            "ProblemTag" => "问题-标签关联",
            "Attachment" => "附件",
            _ => EntityType
        };
    }
}
