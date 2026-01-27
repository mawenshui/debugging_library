using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FieldKb.Infrastructure.SpreadsheetImport;

namespace FieldKb.Client.Wpf;

public sealed partial class SpreadsheetImportViewModel : ObservableObject
{
    private readonly ISpreadsheetImportService _importService;
    private readonly IUiDialogService _dialogService;

    public sealed record Option<T>(T Value, string DisplayName);

    public SpreadsheetImportViewModel(ISpreadsheetImportService importService, IUiDialogService dialogService)
    {
        _importService = importService;
        _dialogService = dialogService;

        PickFileCommand = new AsyncRelayCommand(PickFileAsync);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, false));

        ConflictPolicyOptions = new[]
        {
            new Option<SpreadsheetImportConflictPolicy>(SpreadsheetImportConflictPolicy.SkipIfLocalNewer, "本地更新则跳过并记录冲突"),
            new Option<SpreadsheetImportConflictPolicy>(SpreadsheetImportConflictPolicy.Overwrite, "强制覆盖（不生成冲突）")
        };
        TagMergeModeOptions = new[]
        {
            new Option<SpreadsheetImportTagMergeMode>(SpreadsheetImportTagMergeMode.Replace, "覆盖该问题的标签集合"),
            new Option<SpreadsheetImportTagMergeMode>(SpreadsheetImportTagMergeMode.Merge, "合并到该问题现有标签集合")
        };
        SelectedConflictPolicy = ConflictPolicyOptions[0];
        SelectedTagMergeMode = TagMergeModeOptions[0];

        StatusText = "请选择 .xlsx 或导出包 .zip。";
    }

    public event EventHandler<bool>? RequestClose;

    public IReadOnlyList<Option<SpreadsheetImportConflictPolicy>> ConflictPolicyOptions { get; }
    public IReadOnlyList<Option<SpreadsheetImportTagMergeMode>> TagMergeModeOptions { get; }

    public IAsyncRelayCommand PickFileCommand { get; }
    public IAsyncRelayCommand PreviewCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }
    public IRelayCommand CloseCommand { get; }

    [ObservableProperty]
    private string _importPath = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _previewText = string.Empty;

    [ObservableProperty]
    private Option<SpreadsheetImportConflictPolicy> _selectedConflictPolicy = new(SpreadsheetImportConflictPolicy.SkipIfLocalNewer, "本地更新则跳过并记录冲突");

    [ObservableProperty]
    private Option<SpreadsheetImportTagMergeMode> _selectedTagMergeMode = new(SpreadsheetImportTagMergeMode.Replace, "覆盖该问题的标签集合");

    private async Task PickFileAsync()
    {
        var path = await _dialogService.PickSpreadsheetImportPathAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ImportPath = path;
        await PreviewAsync();
    }

    private async Task PreviewAsync()
    {
        var path = (ImportPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = "请先选择导入文件。";
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "预检中…";
            var report = await _importService.PreviewAsync(
                new SpreadsheetImportRequest(path, SelectedConflictPolicy.Value, SelectedTagMergeMode.Value),
                CancellationToken.None);
            PreviewText = BuildPreviewText(report);
            StatusText = report.Errors.Count == 0 ? "预检完成。" : $"预检完成（{report.Errors.Count} 条提示/错误）。";
        }
        catch (Exception ex)
        {
            StatusText = $"预检失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportAsync()
    {
        var path = (ImportPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = "请先选择导入文件。";
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "导入中…";
            var report = await _importService.ImportAsync(
                new SpreadsheetImportRequest(path, SelectedConflictPolicy.Value, SelectedTagMergeMode.Value),
                CancellationToken.None);
            PreviewText = BuildPreviewText(report);
            StatusText = report.Errors.Count == 0 ? "导入完成。" : $"导入完成（{report.Errors.Count} 条提示/错误）。";
        }
        catch (Exception ex)
        {
            StatusText = $"导入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string BuildPreviewText(SpreadsheetImportReport report)
    {
        var lines = new List<string>
        {
            $"Problems: {report.ProblemsInFile}（已导入：{report.ProblemsImportedCount}）",
            $"Tags: {report.TagsInFile}（已导入：{report.TagsImportedCount}）",
            $"ProblemTags: {report.ProblemTagLinksInFile}（已应用：{report.ProblemTagsAppliedCount}）",
            $"Attachments: {report.AttachmentsInFile}（已导入：{report.AttachmentsImportedCount}，缺失文件：{report.MissingAttachmentFiles}）",
            $"总计：Imported={report.ImportedCount} Skipped={report.SkippedCount} Conflicts={report.ConflictCount}",
            $"耗时：{(report.FinishedAtUtc - report.StartedAtUtc).TotalSeconds:F1}s"
        };

        if (report.Errors.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(report.Errors.Take(20).Select(e => $"- {e}"));
            if (report.Errors.Count > 20)
            {
                lines.Add($"- …（共 {report.Errors.Count} 条）");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
