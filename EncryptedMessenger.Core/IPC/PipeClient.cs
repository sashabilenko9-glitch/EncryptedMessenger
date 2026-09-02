using System.IO.Pipes;
using System.Text;

namespace EncryptedMessenger.Core.IPC
{
    /// <summary>
    /// Named-pipe client used by the WPF app to communicate with the Windows Service.
    /// Reconnects automatically if the connection drops.
    /// </summary>
    public sealed class PipeClient : IDisposable
    {
        public event EventHandler<PipeMessage>? MessageReceived;
        public event EventHandler<bool>? ConnectionChanged; // true = connected

        private NamedPipeClientStream? _pipe;
        private StreamWriter? _writer;
        private CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public bool IsConnected => _pipe?.IsConnected ?? false;

        // ── Lifecycle ─────────────────────────────────────────────────────

        public Task StartAsync() => ConnectLoopAsync(_cts.Token);

        public void Stop() => _cts.Cancel();

        // ── Connect / reconnect loop ──────────────────────────────────────

        private async Task ConnectLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _pipe = new NamedPipeClientStream(
                        ".", PipeServer.PipeName,
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous);

                    await _pipe.ConnectAsync(5_000, ct); // 5 s timeout
                    _writer = new StreamWriter(_pipe, Encoding.UTF8) { AutoFlush = true };

                    ConnectionChanged?.Invoke(this, true);
                    await ReadLoopAsync(_pipe, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { /* service not started yet – retry */ }
                finally
                {
                    ConnectionChanged?.Invoke(this, false);
                    _writer?.Dispose();
                    _pipe?.Dispose();
                }

                if (!ct.IsCancellationRequested)
                    await Task.Delay(3_000, ct).ConfigureAwait(false); // retry delay
            }
        }

        // ── Read loop ─────────────────────────────────────────────────────

        private async Task ReadLoopAsync(PipeStream pipe, CancellationToken ct)
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                try { MessageReceived?.Invoke(this, PipeMessage.FromJson(line)); }
                catch { /* malformed */ }
            }
        }

        // ── Send ──────────────────────────────────────────────────────────

        public async Task SendAsync(PipeMessage message)
        {
            if (_writer == null || !IsConnected)
                throw new InvalidOperationException("Not connected to service.");

            // A NamedPipe stream cannot be written by two operations at once.
            // Connect + GetHistory fire together when a chat opens, so serialise writes.
            await _writeLock.WaitAsync();
            try
            {
                await _writer.WriteLineAsync(message.ToJson());
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose() => Stop();
    }
}