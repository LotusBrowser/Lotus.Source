using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Lotus.Browser
{
    // When Lotus is the default browser, Windows launches a brand new Lotus.exe
    // process every time the user clicks a link elsewhere - it does not know
    // Lotus is already open. This class makes sure only one real instance runs:
    // the first process wins the mutex and starts a named-pipe listener: any
    // later launch just hands its URL off to the pipe and exits immediately,
    // and the running window opens that URL in a new tab.
    public sealed class SingleInstanceManager : IDisposable
    {
        private const string MutexName = "Lotus-SingleInstance-Mutex";
        private const string PipeName = "Lotus-SingleInstance-Pipe";

        private readonly Mutex _mutex;
        private readonly bool _isFirstInstance;
        private CancellationTokenSource _listenerCts;

        public SingleInstanceManager()
        {
            _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out _isFirstInstance);
        }

        public bool IsFirstInstance => _isFirstInstance;

        // Call this once, from the first instance only, right after startup.
        // onUrlReceived is invoked (on the UI thread via the dispatcher you
        // pass in) whenever a second launch forwards a URL to us.
        public void StartListening(Action<string> onUrlReceived)
        {
            _listenerCts = new CancellationTokenSource();
            var token = _listenerCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await server.WaitForConnectionAsync(token);

                        using var reader = new StreamReader(server);
                        string url = await reader.ReadToEndAsync();

                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            onUrlReceived(url.Trim());
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // A single bad connection shouldn't kill the listener loop.
                    }
                }
            }, token);
        }

        // Call this from a second (non-first) launch to hand its command-line
        // URL off to the already-running instance. Fire-and-forget with a
        // short timeout - the caller should Shutdown() right after.
        public static void ForwardUrlToRunningInstance(string url)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(timeout: 2000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.Write(url ?? "");
            }
            catch
            {
                // If the pipe isn't reachable there's nothing more we can do -
                // the caller will just exit without having forwarded anything.
            }
        }

        public void Dispose()
        {
            _listenerCts?.Cancel();
            _mutex?.Dispose();
        }
    }
}
