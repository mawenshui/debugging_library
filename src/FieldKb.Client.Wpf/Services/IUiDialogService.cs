namespace FieldKb.Client.Wpf;

public interface IUiDialogService
{
    Task<string?> PickImportPackagePathAsync();

    Task<string?> PickSpreadsheetImportPathAsync();

    Task<string?> PickExportDirectoryAsync();

    Task<string?> PickSaveFilePathAsync(string filter, string fileName);

    Task<ProblemEditorData?> ShowProblemEditorAsync(ProblemEditorData initialData);

    Task<UserSettingsResult?> ShowUserSettingsAsync(string currentUserName, string currentProfessionId);

    Task<string[]?> PickAttachmentFilePathsAsync();
}
