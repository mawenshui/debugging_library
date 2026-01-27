using System.Collections.ObjectModel;
using System.Collections;
using System.Net;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FieldKb.Client.Wpf;

public partial class ProblemEditorViewModel : ObservableObject, System.ComponentModel.INotifyDataErrorInfo
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public ProblemEditorViewModel(ProblemEditorData initialData, ProfessionProfile profile)
    {
        SaveCommand = new RelayCommand(Save, CanSave);
        CancelCommand = new RelayCommand(Cancel);
        AddEnvironmentRowCommand = new RelayCommand(AddEnvironmentRow);
        RemoveEnvironmentRowCommand = new RelayCommand<EnvironmentEntryRow>(RemoveEnvironmentRow);

        TitleText = initialData.Title;
        Symptom = initialData.Symptom;
        RootCause = initialData.RootCause;
        Solution = initialData.Solution;
        CommonKeys = (profile.CommonKeys ?? Array.Empty<string>()).ToArray();

        var allEntries = EnvironmentJson.TryParseToEntries(initialData.EnvironmentJson);
        var map = allEntries
            .GroupBy(e => (e.Key ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        FixedFields = new ObservableCollection<FixedFieldRow>();
        var fixedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in profile.FixedFields)
        {
            var key = (def.Key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            fixedKeys.Add(key);

            map.TryGetValue(key, out var v);
            var row = new FixedFieldRow(key, def.Label, def.Validation, def.IsRequired, def.Placeholder, v ?? string.Empty);
            row.ErrorsChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
            FixedFields.Add(row);
        }

        EnvironmentEntries = new ObservableCollection<EnvironmentEntryRow>();
        foreach (var entry in allEntries)
        {
            var k = (entry.Key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(k))
            {
                continue;
            }

            if (EnvironmentJson.IsMetaKey(k))
            {
                continue;
            }

            if (fixedKeys.Contains(k))
            {
                continue;
            }

            EnvironmentEntries.Add(new EnvironmentEntryRow(k, entry.Value ?? string.Empty));
        }

        if (EnvironmentEntries.Count == 0)
        {
            foreach (var entry in profile.DefaultCustomEntries ?? Array.Empty<EnvironmentEntry>())
            {
                EnvironmentEntries.Add(new EnvironmentEntryRow(entry.Key ?? string.Empty, entry.Value ?? string.Empty));
            }
        }

        if (EnvironmentEntries.Count == 0)
        {
            EnvironmentEntries.Add(new EnvironmentEntryRow(string.Empty, string.Empty));
        }

        TagOptions = new ObservableCollection<TagOptionRow>(
            (initialData.Tags ?? Array.Empty<TagOption>())
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new TagOptionRow(t.TagId, t.Name, t.IsSelected)));

        ValidateAll();
    }

    [ObservableProperty]
    private string _titleText = string.Empty;

    [ObservableProperty]
    private string _symptom = string.Empty;

    [ObservableProperty]
    private string _rootCause = string.Empty;

    [ObservableProperty]
    private string _solution = string.Empty;

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand AddEnvironmentRowCommand { get; }
    public IRelayCommand RemoveEnvironmentRowCommand { get; }

    public ObservableCollection<FixedFieldRow> FixedFields { get; }
    public ObservableCollection<EnvironmentEntryRow> EnvironmentEntries { get; }
    public IReadOnlyList<string> CommonKeys { get; }
    public ObservableCollection<TagOptionRow> TagOptions { get; }

    public event EventHandler<bool>? RequestClose;
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

    private void Save()
    {
        ValidateAll();
        if (!CanSave())
        {
            return;
        }

        RequestClose?.Invoke(this, true);
    }

    private bool CanSave()
    {
        return !string.IsNullOrWhiteSpace(TitleText) && !HasErrors;
    }

    private void Cancel()
    {
        RequestClose?.Invoke(this, false);
    }

    private void AddEnvironmentRow()
    {
        EnvironmentEntries.Add(new EnvironmentEntryRow(string.Empty, string.Empty));
    }

    private void RemoveEnvironmentRow(EnvironmentEntryRow? row)
    {
        if (row is null)
        {
            return;
        }

        EnvironmentEntries.Remove(row);

        if (EnvironmentEntries.Count == 0)
        {
            EnvironmentEntries.Add(new EnvironmentEntryRow(string.Empty, string.Empty));
        }
    }

    public ProblemEditorData ToData()
    {
        var fixedKeys = new HashSet<string>(FixedFields.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);
        var entries = new List<EnvironmentEntry>();

        foreach (var f in FixedFields)
        {
            var key = (f.Key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = (f.Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            entries.Add(new EnvironmentEntry(key, value));
        }

        foreach (var e in EnvironmentEntries)
        {
            var key = (e.Key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (EnvironmentJson.IsMetaKey(key))
            {
                continue;
            }

            if (fixedKeys.Contains(key))
            {
                continue;
            }

            entries.Add(new EnvironmentEntry(key, (e.Value ?? string.Empty).Trim()));
        }

        var envJson = EnvironmentJson.FromEntries(entries);

        return new ProblemEditorData(
            Title: TitleText.Trim(),
            Symptom: Symptom ?? string.Empty,
            RootCause: RootCause ?? string.Empty,
            Solution: Solution ?? string.Empty,
            EnvironmentJson: envJson,
            Tags: TagOptions.Select(t => new TagOption(t.TagId, t.Name, t.IsSelected)).ToArray());
    }

    private void ValidateAll()
    {
        ValidateTitle();
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnTitleTextChanged(string value)
    {
        ValidateTitle();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void ValidateTitle()
    {
        SetErrors(nameof(TitleText), null);
        if (string.IsNullOrWhiteSpace(TitleText))
        {
            SetErrors(nameof(TitleText), new[] { "标题不能为空。" });
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

    public sealed partial class EnvironmentEntryRow : ObservableObject
    {
        public EnvironmentEntryRow(string key, string value)
        {
            _key = key;
            _value = value;
        }

        [ObservableProperty]
        private string _key;

        [ObservableProperty]
        private string _value;
    }

    public sealed partial class FixedFieldRow : ObservableObject, INotifyDataErrorInfo
    {
        private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

        public FixedFieldRow(string key, string label, FixedFieldValidation validation, bool isRequired, string? placeholder, string value)
        {
            _key = key;
            _label = label;
            _validation = validation;
            _isRequired = isRequired;
            _placeholder = placeholder;
            _value = value;
            Validate();
        }

        [ObservableProperty]
        private string _key;

        [ObservableProperty]
        private string _label;

        [ObservableProperty]
        private FixedFieldValidation _validation;

        [ObservableProperty]
        private bool _isRequired;

        [ObservableProperty]
        private string? _placeholder;

        [ObservableProperty]
        private string _value = string.Empty;

        public bool HasErrors => _errors.Count > 0;

        public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

        public IEnumerable GetErrors(string? propertyName)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return _errors.Values.SelectMany(static x => x).ToArray();
            }

            return _errors.TryGetValue(propertyName, out var list) ? list : Array.Empty<string>();
        }

        partial void OnValueChanged(string value)
        {
            Validate();
        }

        private void Validate()
        {
            SetErrors(nameof(Value), null);

            var v = (Value ?? string.Empty).Trim();
            if (IsRequired && string.IsNullOrWhiteSpace(v))
            {
                SetErrors(nameof(Value), new[] { $"{Label}不能为空。" });
                return;
            }

            if (string.IsNullOrWhiteSpace(v))
            {
                return;
            }

            if (Validation == FixedFieldValidation.IpAddress)
            {
                if (!IPAddress.TryParse(v, out _))
                {
                    SetErrors(nameof(Value), new[] { "IP 地址格式不正确。" });
                }
                return;
            }

            if (Validation == FixedFieldValidation.Port)
            {
                if (!int.TryParse(v, out var port) || port is < 1 or > 65535)
                {
                    SetErrors(nameof(Value), new[] { "端口需为 1-65535。" });
                }
            }
        }

        private void SetErrors(string propertyName, IEnumerable<string>? errors)
        {
            if (errors is null)
            {
                if (_errors.Remove(propertyName))
                {
                    ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
                }

                return;
            }

            var newErrors = errors.Where(static e => !string.IsNullOrWhiteSpace(e)).ToList();
            if (newErrors.Count == 0)
            {
                if (_errors.Remove(propertyName))
                {
                    ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
                }

                return;
            }

            _errors[propertyName] = newErrors;
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        }
    }

    public sealed partial class TagOptionRow : ObservableObject
    {
        public TagOptionRow(string tagId, string name, bool isSelected)
        {
            _tagId = tagId;
            _name = name;
            _isSelected = isSelected;
        }

        [ObservableProperty]
        private string _tagId;

        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private bool _isSelected;
    }
}
