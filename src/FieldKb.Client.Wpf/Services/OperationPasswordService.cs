using System.Security.Cryptography;
using System.Threading;

namespace FieldKb.Client.Wpf;

public sealed class OperationPasswordService
{
    private const int DefaultIterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    private readonly IAppSettingsStore _store;
    private OperationPasswordConfig? _cached;
    private bool _loaded;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public OperationPasswordService(IAppSettingsStore store)
    {
        _store = store;
    }

    public bool IsConfigured
    {
        get
        {
            lock (_gate)
            {
                return _cached is not null;
            }
        }
    }

    public bool Verify(string password)
    {
        OperationPasswordConfig? cfg;
        lock (_gate)
        {
            cfg = _cached;
        }

        if (cfg is null)
        {
            return false;
        }

        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(cfg.SaltBase64);
            expected = Convert.FromBase64String(cfg.HashBase64);
        }
        catch
        {
            return false;
        }

        var actual = Derive(password, salt, cfg.Iterations);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public async Task SetAsync(string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, DefaultIterations);
        var cfg = new OperationPasswordConfig(Convert.ToBase64String(salt), Convert.ToBase64String(hash), DefaultIterations);
        await _store.WriteOperationPasswordAsync(cfg, cancellationToken);
        lock (_gate)
        {
            _cached = cfg;
            _loaded = true;
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

            var cfg = await _store.ReadOperationPasswordAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _cached = cfg;
                _loaded = true;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(HashSize);
    }
}
