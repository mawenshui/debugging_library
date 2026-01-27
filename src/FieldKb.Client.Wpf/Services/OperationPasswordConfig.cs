namespace FieldKb.Client.Wpf;

public sealed record OperationPasswordConfig(string SaltBase64, string HashBase64, int Iterations);

