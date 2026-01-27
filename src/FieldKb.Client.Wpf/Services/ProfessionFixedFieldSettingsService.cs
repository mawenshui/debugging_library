using System.Threading;

namespace FieldKb.Client.Wpf;

public sealed class ProfessionFixedFieldSettingsService : IProfessionFixedFieldSettings
{
    private readonly IAppSettingsStore _appSettingsStore;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _loaded;
    private Dictionary<string, IReadOnlyList<ProfessionFixedFieldSetting>> _selected = new(StringComparer.OrdinalIgnoreCase);

    public ProfessionFixedFieldSettingsService(IAppSettingsStore appSettingsStore)
    {
        _appSettingsStore = appSettingsStore;
    }

    public IReadOnlyList<ProfessionFixedFieldSetting> GetSelectedFixedFields(string professionId)
    {
        if (!_loaded)
        {
            return Array.Empty<ProfessionFixedFieldSetting>();
        }

        var pid = ProfessionIds.Normalize(professionId);
        return _selected.TryGetValue(pid, out var list) ? list : Array.Empty<ProfessionFixedFieldSetting>();
    }

    public void SetSelectedFixedFields(string professionId, IReadOnlyList<ProfessionFixedFieldSetting> fixedFields)
    {
        if (!_loaded)
        {
            return;
        }

        var pid = ProfessionIds.Normalize(professionId);
        lock (_gate)
        {
            _selected[pid] = fixedFields?.ToArray() ?? Array.Empty<ProfessionFixedFieldSetting>();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            var map = await _appSettingsStore.ReadProfessionFixedFieldsAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _selected = new Dictionary<string, IReadOnlyList<ProfessionFixedFieldSetting>>(map, StringComparer.OrdinalIgnoreCase);
                _loaded = true;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task SaveAsync(string professionId, CancellationToken cancellationToken)
    {
        if (!_loaded)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        var pid = ProfessionIds.Normalize(professionId);
        IReadOnlyList<ProfessionFixedFieldSetting> list;
        lock (_gate)
        {
            list = _selected.TryGetValue(pid, out var existing) ? existing : Array.Empty<ProfessionFixedFieldSetting>();
        }

        await _appSettingsStore.WriteProfessionFixedFieldsAsync(pid, list, cancellationToken);
    }
}
