using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FieldKb.Client.Wpf;

public sealed partial class AboutViewModel : ObservableObject
{
    public AboutViewModel()
    {
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));

        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        AppName = ReadProduct(asm) ?? ReadTitle(asm) ?? asm.GetName().Name ?? "应用";
        Version = ReadInformationalVersion(asm) ?? asm.GetName().Version?.ToString() ?? "unknown";
        Author = ReadCompany(asm) ?? "unknown";
        Runtime = $".NET {Environment.Version}";
        Os = Environment.OSVersion.ToString();
    }

    public event EventHandler? RequestClose;

    public IRelayCommand CloseCommand { get; }

    [ObservableProperty]
    private string _appName = string.Empty;

    [ObservableProperty]
    private string _version = string.Empty;

    [ObservableProperty]
    private string _author = string.Empty;

    [ObservableProperty]
    private string _runtime = string.Empty;

    [ObservableProperty]
    private string _os = string.Empty;

    private static string? ReadProduct(Assembly asm) => asm.GetCustomAttribute<AssemblyProductAttribute>()?.Product;

    private static string? ReadTitle(Assembly asm) => asm.GetCustomAttribute<AssemblyTitleAttribute>()?.Title;

    private static string? ReadCompany(Assembly asm) => asm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;

    private static string? ReadInformationalVersion(Assembly asm) => asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
}

