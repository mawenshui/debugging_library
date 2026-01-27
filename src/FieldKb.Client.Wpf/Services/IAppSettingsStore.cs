namespace FieldKb.Client.Wpf;

public interface IAppSettingsStore
{
    Task<string?> ReadUserNameAsync(CancellationToken cancellationToken);

    Task WriteUserNameAsync(string userName, CancellationToken cancellationToken);

    Task<string?> ReadProfessionIdAsync(CancellationToken cancellationToken);

    Task WriteProfessionIdAsync(string professionId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, IReadOnlyList<ProfessionFixedFieldSetting>>> ReadProfessionFixedFieldsAsync(CancellationToken cancellationToken);

    Task WriteProfessionFixedFieldsAsync(string professionId, IReadOnlyList<ProfessionFixedFieldSetting> fixedFields, CancellationToken cancellationToken);

    Task<OperationPasswordConfig?> ReadOperationPasswordAsync(CancellationToken cancellationToken);

    Task WriteOperationPasswordAsync(OperationPasswordConfig? passwordConfig, CancellationToken cancellationToken);
}
