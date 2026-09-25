using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncryptedMessenger.Core.IPC
{
    /// <summary>
    /// Named-pipe server that the Windows Service hosts.
    /// The WPF application connects as a client to send commands and receive events.
    ///
    /// Each UI connection gets its own pipe instance (multi-client support).
    /// </summary>
    public sealed class PipeServer : IDisposable
    {
        public const string PipeName = "EncryptedMessengerPipe";

        public event EventHandler<PipeMessage>? MessageReceived;

        private readonly ILogger _logger;
        private CancellationTokenSource _cts = new();

        // All active writer streams (one per connected UI instance)
        private readonly List<PipeStream> _clients = [];
        private readonly object _clientsLock = new();

        // Broadcasts are triggered concurrently from network, discovery and pipe handlers.
        // Two overlapping writes to the same pipe could interleave JSON lines, so only one
        // broadcast writes at a time.
        private readonly SemaphoreSlim _broadcastLock = new(1, 1);

        public PipeServer(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────

        public Task StartAsync() => AcceptLoopAsync(_cts.Token);

        public void Stop() => _cts.Cancel();

        // ── Accept loop ───────────────────────────────────────────────────

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                // CurrentUserOnly: the pipe's ACL grants access to this Windows user only.
                // Without it Windows' default pipe ACL lets other accounts on the machine
                // connect and read our broadcasts — which contain decrypted message text.
                var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                try
                {
                    await pipe.WaitForConnectionAsync(ct);
                    lock (_clientsLock) _clients.Add(pipe);
                    _logger.LogInformation("UI client connected ({Count} active)", _clients.Count);
                    _ = Task.Run(() => HandleClientAsync(pipe, ct), ct);
                }
                catch (OperationCanceledException) { pipe.Dispose(); break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Pipe accept error");
                    pipe.Dispose();
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    try { MessageReceived?.Invoke(this, PipeMessage.FromJson(line)); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Malformed pipe message: {Line}", line); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Pipe client handler error"); }
            finally
            {
                lock (_clientsLock) _clients.Remove(pipe);
                pipe.Dispose();
                _logger.LogInformation("UI client disconnected");
            }
        }

        // ── Broadcast to all connected UI clients ─────────────────────────

        public async Task BroadcastAsync(PipeMessage message)
        {
            var json = message.ToJson() + "\n";
            var data = Encoding.UTF8.GetBytes(json);

            List<PipeStream> snapshot;
            lock (_clientsLock) snapshot = [.._clients];

            await _broadcastLock.WaitAsync();
            try
            {
                foreach (var client in snapshot)
                {
                    try { await client.WriteAsync(data); await client.FlushAsync(); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Broadcast to a client failed (likely disconnected)"); }
                }
            }
            finally { _broadcastLock.Release(); }
        }

        public void Dispose() => Stop();
    }
}
