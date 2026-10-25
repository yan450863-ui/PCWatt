using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PCWatt.Infrastructure.Windows;

/// <summary>The mutex is acquired and released on its owning UI thread.</summary>
[SupportedOSPlatform("windows")]
public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;
    private bool _disposed;
    public bool IsPrimary { get; }

    public SingleInstanceCoordinator(string? testIdentity = null)
    {
        var identity = testIdentity ?? CurrentUserIdentity();
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        _pipeName = "PCWatt-" + suffix;
        _mutex = new Mutex(true, @"Global\" + _pipeName, out var created);
        try { IsPrimary = created || _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }

    public void Listen(Action onOpen)
    {
        if (!IsPrimary || _listener is not null) return;
        _listener = Task.Run(async () =>
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_shutdown.Token);
                    var message = new byte[1];
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    if (await pipe.ReadAsync(message, timeout.Token) == 1 && message[0] == 1) onOpen();
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        });
    }

    private static string CurrentUserIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? Environment.UserName;
    }

    public async Task NotifyPrimaryAsync()
    {
        await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000);
        await client.WriteAsync(new byte[] { 1 });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _listener?.GetAwaiter().GetResult();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _shutdown.Dispose();
    }
}
