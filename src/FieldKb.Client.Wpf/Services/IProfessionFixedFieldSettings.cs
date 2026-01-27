namespace FieldKb.Client.Wpf;

public interface IProfessionFixedFieldSettings
{
    Task InitializeAsync(CancellationToken cancellationToken);

    IReadOnlyList<ProfessionFixedFieldSetting> GetSelectedFixedFields(string professionId);

    void SetSelectedFixedFields(string professionId, IReadOnlyList<ProfessionFixedFieldSetting> fixedFields);

    Task SaveAsync(string professionId, CancellationToken cancellationToken);
}
