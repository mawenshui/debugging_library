using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FieldKb.Client.Wpf;

public sealed partial class UserSettingsViewModel : ObservableObject, System.ComponentModel.INotifyDataErrorInfo
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public UserSettingsViewModel(string currentUserName, string currentProfessionId)
    {
        SaveCommand = new RelayCommand(Save, CanSave);
        CancelCommand = new RelayCommand(Cancel);
        OpenProfessionSettingsCommand = new RelayCommand(OpenProfessionSettings);
        OpenOperationPasswordCommand = new RelayCommand(OpenOperationPassword);
        OpenDataPurgeCommand = new RelayCommand(OpenDataPurge);
        UserName = currentUserName;
        ProfessionId = ProfessionIds.Normalize(currentProfessionId);
        ProfessionOptions = ProfessionIds.Options;
        ValidateUserName();
        ValidateProfessionId();
    }

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _professionId = ProfessionIds.General;

    public IReadOnlyList<ProfessionOption> ProfessionOptions { get; }

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand OpenProfessionSettingsCommand { get; }
    public IRelayCommand OpenOperationPasswordCommand { get; }
    public IRelayCommand OpenDataPurgeCommand { get; }

    public event EventHandler<bool>? RequestClose;
    public event EventHandler? RequestOpenProfessionSettings;
    public event EventHandler? RequestOpenOperationPassword;
    public event EventHandler? RequestOpenDataPurge;
    public event EventHandler<System.ComponentModel.DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => _errors.Count > 0;

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return _errors.Values.SelectMany(static x => x).ToArray();
        }

        return _errors.TryGetValue(propertyName, out var list) ? list : Array.Empty<string>();
    }

    partial void OnUserNameChanged(string value)
    {
        ValidateUserName();
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnProfessionIdChanged(string value)
    {
        ValidateProfessionId();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void Save()
    {
        ValidateUserName();
        ValidateProfessionId();
        if (!CanSave())
        {
            return;
        }

        RequestClose?.Invoke(this, true);
    }

    private bool CanSave()
    {
        return !HasErrors;
    }

    private void Cancel()
    {
        RequestClose?.Invoke(this, false);
    }

    private void OpenProfessionSettings()
    {
        RequestOpenProfessionSettings?.Invoke(this, EventArgs.Empty);
    }

    private void OpenOperationPassword()
    {
        RequestOpenOperationPassword?.Invoke(this, EventArgs.Empty);
    }

    private void OpenDataPurge()
    {
        RequestOpenDataPurge?.Invoke(this, EventArgs.Empty);
    }

    private void ValidateUserName()
    {
        SetErrors(nameof(UserName), null);
        if (!UserNameRules.IsValid(UserName, out var error))
        {
            SetErrors(nameof(UserName), new[] { error! });
        }
    }

    private void ValidateProfessionId()
    {
        SetErrors(nameof(ProfessionId), null);
        var normalized = ProfessionIds.Normalize(ProfessionId);
        if (!ProfessionIds.IsValid(normalized))
        {
            SetErrors(nameof(ProfessionId), new[] { "职业不合法。" });
            return;
        }

        if (!string.Equals(ProfessionId, normalized, StringComparison.Ordinal))
        {
            ProfessionId = normalized;
        }
    }

    private void SetErrors(string propertyName, IEnumerable<string>? errors)
    {
        if (errors is null)
        {
            if (_errors.Remove(propertyName))
            {
                ErrorsChanged?.Invoke(this, new System.ComponentModel.DataErrorsChangedEventArgs(propertyName));
            }

            return;
        }

        var newErrors = errors.Where(static e => !string.IsNullOrWhiteSpace(e)).ToList();
        if (newErrors.Count == 0)
        {
            if (_errors.Remove(propertyName))
            {
                ErrorsChanged?.Invoke(this, new System.ComponentModel.DataErrorsChangedEventArgs(propertyName));
            }

            return;
        }

        _errors[propertyName] = newErrors;
        ErrorsChanged?.Invoke(this, new System.ComponentModel.DataErrorsChangedEventArgs(propertyName));
    }
}
