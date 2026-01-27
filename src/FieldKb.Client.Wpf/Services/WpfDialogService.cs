using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace FieldKb.Client.Wpf;

public sealed class WpfDialogService : IUiDialogService
{
    private readonly IUserContext _userContext;
    private readonly IProfessionProfileProvider _professionProfileProvider;
    private readonly ProfessionProfileProvider _baseProfessionProfileProvider;
    private readonly IProfessionFixedFieldSettings _professionFixedFieldSettings;
    private readonly OperationPasswordService _operationPasswordService;
    private readonly FieldKb.Application.Abstractions.IKbStore _store;

    public WpfDialogService(
        IUserContext userContext,
        IProfessionProfileProvider professionProfileProvider,
        ProfessionProfileProvider baseProfessionProfileProvider,
        IProfessionFixedFieldSettings professionFixedFieldSettings,
        OperationPasswordService operationPasswordService,
        FieldKb.Application.Abstractions.IKbStore store)
    {
        _userContext = userContext;
        _professionProfileProvider = professionProfileProvider;
        _baseProfessionProfileProvider = baseProfessionProfileProvider;
        _professionFixedFieldSettings = professionFixedFieldSettings;
        _operationPasswordService = operationPasswordService;
        _store = store;
    }

    private static Window? GetDialogOwner()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return null;
        }

        var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return active ?? app.MainWindow;
    }

    public Task<string?> PickImportPackagePathAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Knowledge Base Package (*.zip)|*.zip|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    public Task<string?> PickSpreadsheetImportPathAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Spreadsheet Import (*.zip;*.xlsx)|*.zip;*.xlsx|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    public Task<string?> PickExportDirectoryAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Knowledge Base Package (*.zip)|*.zip|All Files (*.*)|*.*",
            FileName = "kbpkg.zip",
            OverwritePrompt = false
        };

        if (dialog.ShowDialog() != true)
        {
            return Task.FromResult<string?>(null);
        }

        var dir = Path.GetDirectoryName(dialog.FileName);
        return Task.FromResult(dir);
    }

    public Task<string?> PickSaveFilePathAsync(string filter, string fileName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = string.IsNullOrWhiteSpace(filter) ? "All Files (*.*)|*.*" : filter,
            FileName = string.IsNullOrWhiteSpace(fileName) ? "export" : fileName,
            OverwritePrompt = true
        };

        return Task.FromResult(dialog.ShowDialog(GetDialogOwner()) == true ? dialog.FileName : null);
    }

    public Task<ProblemEditorData?> ShowProblemEditorAsync(ProblemEditorData initialData)
    {
        var profile = _professionProfileProvider.GetProfile(_userContext.CurrentProfessionId);
        var vm = new ProblemEditorViewModel(initialData, profile);
        var owner = GetDialogOwner();
        var window = new ProblemEditorWindow
        {
            Owner = owner,
            DataContext = vm
        };
        if (owner is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        var ok = window.ShowDialog() == true;
        return Task.FromResult(ok ? vm.ToData() : null);
    }

    public Task<UserSettingsResult?> ShowUserSettingsAsync(string currentUserName, string currentProfessionId)
    {
        var vm = new UserSettingsViewModel(currentUserName, currentProfessionId);
        var owner = GetDialogOwner();
        var window = new UserSettingsWindow
        {
            Owner = owner,
            DataContext = vm
        };
        if (owner is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        vm.RequestOpenProfessionSettings += (_, _) =>
        {
            var pvm = new ProfessionFixedFieldsViewModel(_baseProfessionProfileProvider, _professionFixedFieldSettings);
            var pwindow = new ProfessionFixedFieldsWindow
            {
                Owner = window,
                DataContext = pvm
            };
            pwindow.ShowDialog();
        };

        vm.RequestOpenOperationPassword += (_, _) =>
        {
            var pvm = new OperationPasswordViewModel(_operationPasswordService);
            var pwindow = new OperationPasswordWindow
            {
                Owner = window,
                DataContext = pvm
            };
            pwindow.ShowDialog();
        };

        vm.RequestOpenDataPurge += (_, _) =>
        {
            var dvm = new DataPurgeViewModel(_store, _operationPasswordService);
            var dwindow = new DataPurgeWindow
            {
                Owner = window,
                DataContext = dvm
            };
            dwindow.ShowDialog();
        };

        var ok = window.ShowDialog() == true;
        return Task.FromResult(ok
            ? new UserSettingsResult(UserNameRules.Normalize(vm.UserName), ProfessionIds.Normalize(vm.ProfessionId))
            : null);
    }

    public Task<string[]?> PickAttachmentFilePathsAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileNames : null);
    }
}
