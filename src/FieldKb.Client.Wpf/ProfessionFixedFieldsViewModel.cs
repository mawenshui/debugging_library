using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FieldKb.Client.Wpf;

public sealed partial class ProfessionFixedFieldsViewModel : ObservableObject
{
    private const int MaxSelected = 8;
    private readonly ProfessionProfileProvider _baseProfiles;
    private readonly IProfessionFixedFieldSettings _settings;

    public ProfessionFixedFieldsViewModel(ProfessionProfileProvider baseProfiles, IProfessionFixedFieldSettings settings)
    {
        _baseProfiles = baseProfiles;
        _settings = settings;

        Professions = ProfessionIds.Options;
        FieldOptions = new ObservableCollection<FieldOption>();

        LoadCommand = new AsyncRelayCommand(LoadAsync);
        AddFieldCommand = new RelayCommand(AddField);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, false));
    }

    public event EventHandler<bool>? RequestClose;

    public IReadOnlyList<ProfessionOption> Professions { get; }
    public ObservableCollection<FieldOption> FieldOptions { get; }

    public IAsyncRelayCommand LoadCommand { get; }
    public IRelayCommand AddFieldCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }

    [ObservableProperty]
    private string _selectedProfessionId = ProfessionIds.General;

    [ObservableProperty]
    private string _newFieldName = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _selectedCountText = $"0 / {MaxSelected}";

    partial void OnSelectedProfessionIdChanged(string value)
    {
        LoadProfessionFields(value);
    }

    private async Task LoadAsync()
    {
        try
        {
            await _settings.InitializeAsync(CancellationToken.None);
            LoadProfessionFields(SelectedProfessionId);
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
    }

    private void LoadProfessionFields(string professionId)
    {
        var pid = ProfessionIds.Normalize(professionId);
        var profile = _baseProfiles.GetProfile(pid);
        var selected = _settings.GetSelectedFixedFields(pid)
            .Select(x => x.Key)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        FieldOptions.Clear();
        foreach (var f in profile.FixedFields)
        {
            var option = new FieldOption(f.Key, f.Label) { IsSelected = selected.Contains(f.Key) };
            option.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FieldOption.IsSelected))
                {
                    EnforceSelectionLimit(option);
                    UpdateSelectedCountText();
                }
            };
            FieldOptions.Add(option);
        }

        var extra = _settings.GetSelectedFixedFields(pid)
            .Where(x => !profile.FixedFields.Any(b => string.Equals(b.Key, x.Key, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        foreach (var e in extra)
        {
            var option = new FieldOption(e.Key, e.Label) { IsSelected = true };
            option.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(FieldOption.IsSelected))
                {
                    EnforceSelectionLimit(option);
                    UpdateSelectedCountText();
                }
            };
            FieldOptions.Add(option);
        }

        UpdateSelectedCountText();
    }

    private void EnforceSelectionLimit(FieldOption changed)
    {
        if (!changed.IsSelected)
        {
            return;
        }

        var selectedCount = FieldOptions.Count(x => x.IsSelected);
        if (selectedCount <= MaxSelected)
        {
            return;
        }

        changed.IsSelected = false;
        StatusText = $"最多只能选择 {MaxSelected} 个固定字段。";
    }

    private void UpdateSelectedCountText()
    {
        var selectedCount = FieldOptions.Count(x => x.IsSelected);
        SelectedCountText = $"{selectedCount} / {MaxSelected}";
    }

    private void AddField()
    {
        var label = (NewFieldName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            StatusText = "字段名称不能为空。";
            return;
        }

        if (FieldOptions.Any(x => string.Equals(x.DisplayName, label, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "已存在同名字段。";
            return;
        }

        var option = new FieldOption($"custom_{Guid.NewGuid():N}", label) { IsSelected = true };
        option.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FieldOption.IsSelected))
            {
                EnforceSelectionLimit(option);
                UpdateSelectedCountText();
            }
        };
        FieldOptions.Add(option);
        NewFieldName = string.Empty;
        EnforceSelectionLimit(option);
        UpdateSelectedCountText();
    }

    private async Task SaveAsync()
    {
        var pid = ProfessionIds.Normalize(SelectedProfessionId);
        var selected = FieldOptions
            .Where(x => x.IsSelected)
            .Select(x => new ProfessionFixedFieldSetting(x.Key, x.DisplayName))
            .ToArray();

        if (selected.Length > MaxSelected)
        {
            StatusText = $"最多只能选择 {MaxSelected} 个固定字段。";
            return;
        }

        try
        {
            _settings.SetSelectedFixedFields(pid, selected);
            await _settings.SaveAsync(pid, CancellationToken.None);
            StatusText = "已保存职业设置。";
            RequestClose?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    public sealed partial class FieldOption : ObservableObject
    {
        public FieldOption(string key, string displayName)
        {
            Key = key;
            DisplayName = displayName;
        }

        public string Key { get; }

        public string DisplayName { get; }

        [ObservableProperty]
        private bool _isSelected;
    }
}
