namespace FieldKb.Client.Wpf;

public sealed class CustomizableProfessionProfileProvider : IProfessionProfileProvider
{
    private readonly ProfessionProfileProvider _baseProvider;
    private readonly IProfessionFixedFieldSettings _fixedFieldSettings;

    public CustomizableProfessionProfileProvider(ProfessionProfileProvider baseProvider, IProfessionFixedFieldSettings fixedFieldSettings)
    {
        _baseProvider = baseProvider;
        _fixedFieldSettings = fixedFieldSettings;
    }

    public ProfessionProfile GetProfile(string professionId)
    {
        var baseProfile = _baseProvider.GetProfile(professionId);
        var pid = ProfessionIds.Normalize(professionId);
        var selected = _fixedFieldSettings.GetSelectedFixedFields(pid);
        if (selected.Count == 0)
        {
            return baseProfile;
        }

        var baseMap = baseProfile.FixedFields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
        var list = new List<FixedFieldDefinition>();
        foreach (var s in selected.Take(8))
        {
            var key = (s.Key ?? string.Empty).Trim();
            var label = (s.Label ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            if (EnvironmentJson.IsMetaKey(key))
            {
                continue;
            }

            if (baseMap.TryGetValue(key, out var def))
            {
                list.Add(def);
                continue;
            }

            list.Add(new FixedFieldDefinition(key, label, FixedFieldValidation.None));
        }

        if (list.Count == 0)
        {
            return baseProfile;
        }

        return baseProfile with { FixedFields = list };
    }
}

