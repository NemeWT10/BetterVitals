using System.IO;
using System.IO.Pipes;
using System.Text;
using BetterVitals.Core;

namespace BetterVitals.Services;

/// <summary>
/// One process per data folder (the key is <see cref="AppEnv.InstanceKey"/>, so a test instance never talks to the
/// user's app): a named mutex decides, a second launch sends one command over a named pipe (current user only) and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;

    public bool IsPrimary { get; }

    public SingleInstance(string key)
    {
        _pipeName = key + "-pipe";
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + key, out var created);
        IsPrimary = created;
    }

    /// <summary>Hands a command ("open:overview") to the running instance. False when it did not answer.</summary>
    public bool SendToPrimary(string command)
    {
        try
        {
            // the primary may bring its window to the front (we are the foreground process right now)
            UI.Native.AllowSetForegroundWindow(UI.Native.ASFW_ANY);
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(command);
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"single instance: handoff failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Listens for commands of later launches; <paramref name="onCommand"/> runs on a pool thread.</summary>
    public void Listen(Action<string> onCommand)
    {
        if (!IsPrimary || _cts is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var ms = new MemoryStream();
                    var buffer = new byte[512];
                    int n;
                    while (ms.Length < 4096 && (n = await server.ReadAsync(buffer, token)) > 0) ms.Write(buffer, 0, n);
                    var command = Encoding.UTF8.GetString(ms.ToArray()).Trim();
                    if (command.Length > 0) onCommand(command);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn($"single instance: listener error: {ex.Message}");
                    try
                    {
                        await Task.Delay(500, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // not owned by this thread
            }
        }
        _mutex.Dispose();
    }
}
