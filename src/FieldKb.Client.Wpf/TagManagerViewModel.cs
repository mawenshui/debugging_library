using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FieldKb.Application.Abstractions;
using FieldKb.Domain.Models;

namespace FieldKb.Client.Wpf;

public sealed partial class TagManagerViewModel : ObservableObject
{
    private readonly IKbStore _store;
    private readonly string _updatedByInstanceId;

    public TagManagerViewModel(IKbStore store, string updatedByInstanceId)
    {
        _store = store;
        _updatedByInstanceId = updatedByInstanceId;
        Tags = new ObservableCollection<TagItem>();
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CreateTagCommand = new AsyncRelayCommand(CreateTagAsync);
        DeleteTagCommand = new AsyncRelayCommand(DeleteTagAsync);
        _ = RefreshAsync();
    }

    [ObservableProperty]
    private string _newTagName = string.Empty;

    [ObservableProperty]
    private TagItem? _selectedTag;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public ObservableCollection<TagItem> Tags { get; }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand CreateTagCommand { get; }
    public IAsyncRelayCommand DeleteTagCommand { get; }

    private async Task RefreshAsync()
    {
        IReadOnlyList<Tag> tags;
        try
        {
            tags = await DbBusyUiRetry.RunAsync(ct => _store.GetAllTagsAsync(ct), actionName: "刷新标签", ct: CancellationToken.None);
        }
        catch
        {
            return;
        }
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Tags.Clear();
            foreach (var t in tags)
            {
                Tags.Add(new TagItem(t.Id, t.Name));
            }
        });
    }

    private async Task CreateTagAsync()
    {
        var name = (NewTagName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText = "标签名不能为空。";
            return;
        }

        try
        {
            await DbBusyUiRetry.RunAsync(ct => _store.CreateTagAsync(name, DateTimeOffset.UtcNow, _updatedByInstanceId, ct), actionName: "创建标签", ct: CancellationToken.None);
            NewTagName = string.Empty;
            StatusText = "已创建标签。";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"创建失败：{ex.Message}";
        }
    }

    private async Task DeleteTagAsync()
    {
        if (SelectedTag is null)
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"确定要删除标签“{SelectedTag.Name}”吗？（软删除）",
            "确认删除",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await DbBusyUiRetry.RunAsync(ct => _store.SoftDeleteTagAsync(SelectedTag.TagId, DateTimeOffset.UtcNow, _updatedByInstanceId, ct), actionName: "删除标签", ct: CancellationToken.None);
            StatusText = "已删除标签。";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"删除失败：{ex.Message}";
        }
    }

    public sealed record TagItem(string TagId, string Name);
}
