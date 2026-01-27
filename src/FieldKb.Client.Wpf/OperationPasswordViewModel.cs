using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FieldKb.Client.Wpf;

public sealed partial class OperationPasswordViewModel : ObservableObject
{
    private readonly OperationPasswordService _service;

    public OperationPasswordViewModel(OperationPasswordService service)
    {
        _service = service;
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, false));
    }

    public event EventHandler<bool>? RequestClose;

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }

    [ObservableProperty]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    private string _confirmPassword = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    private async Task LoadAsync()
    {
        try
        {
            await _service.InitializeAsync(CancellationToken.None);
            StatusText = _service.IsConfigured ? "已配置操作密码。" : "尚未配置操作密码。";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
    }

    private async Task SaveAsync()
    {
        var p1 = NewPassword ?? string.Empty;
        var p2 = ConfirmPassword ?? string.Empty;
        if (string.IsNullOrWhiteSpace(p1))
        {
            StatusText = "密码不能为空。";
            return;
        }

        if (!string.Equals(p1, p2, StringComparison.Ordinal))
        {
            StatusText = "两次输入的密码不一致。";
            return;
        }

        try
        {
            await _service.SetAsync(p1, CancellationToken.None);
            StatusText = "已保存操作密码。";
            RequestClose?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }
}
