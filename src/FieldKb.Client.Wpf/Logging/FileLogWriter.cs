using System.IO;
using System.Text;
using System.Threading.Channels;

namespace FieldKb.Client.Wpf;

public sealed class FileLogWriter : IDisposable
{
    private readonly Channel<string> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _worker;
    private readonly string _path;

    public FileLogWriter(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _cts = new CancellationTokenSource();
        _worker = Task.Run(async () => await RunAsync(_cts.Token));
    }

    public void Enqueue(string line)
    {
        _channel.Writer.TryWrite(line);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        await foreach (var line in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            await writer.WriteLineAsync(line);
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();

        try
        {
            if (!_worker.Wait(TimeSpan.FromSeconds(3)))
            {
                _cts.Cancel();
                _ = _worker.Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch
        {
        }
        finally
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
