using System.Net;
using System.Net.Sockets;
using EncryptedMessenger.Core.Encryption;
using EncryptedMessenger.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncryptedMessenger.Core.Network
{
    /// <summary>
    /// Listens for incoming TCP connections from other messenger instances.
    /// </summary>
    public sealed class MessengerServer : IDisposable
    {
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ContactStatusEventArgs>? ContactConnected;
        public event EventHandler<ContactStatusEventArgs>? ContactDisconnected;
        public event EventHandler<DeliveryAckEventArgs>? DeliveryAcknowledged;

        private readonly int _port;
        private readonly string _ownId;
        private readonly string _ownDisplayName;
        private readonly CryptoManager _crypto;
        private readonly ILogger _logger;

        private TcpListener? _listener;
        private CancellationTokenSource _cts = new();

        private readonly Dictionary<string, NetworkStream> _activeStreams = new();
        private readonly object _streamsLock = new();

        public MessengerServer(int port, string ownId, string ownDisplayName, CryptoManager crypto, ILogger? logger = null)
        {
            _port = port;
            _ownId = ownId;
            _ownDisplayName = ownDisplayName;
            _crypto = crypto;
            _logger = logger ?? NullLogger.Instance;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────

        public async Task StartAsync()
        {
            _cts = new CancellationTokenSource();
            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                _logger.LogInformation("Listening on 0.0.0.0:{Port} (id={ContactId})", _port, Short(_ownId));
                await AcceptLoopAsync(_cts.Token);
            }
            catch (SocketException ex)
            {
                // Most common: port already in use (another copy on same machine same port)
                _logger.LogError(ex, "Start failed on port {Port}: {SocketError}", _port, ex.SocketErrorCode);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Start failed on port {Port}", _port);
                throw;
            }
        }

        public void Stop()
        {
            _cts.Cancel();
            _listener?.Stop();
            _logger.LogInformation("Server stopped");
        }

        // ── Accept loop ───────────────────────────────────────────────────

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync(ct);
                    client.NoDelay = true;
                    var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
                    _logger.LogInformation("Incoming connection from {Remote}", remote);
                    _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Accept error");
                }
            }
        }

        // ── Per-client handler ────────────────────────────────────────────

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            string? contactId = null;
            var stream = client.GetStream();

            try
            {
                // 1. Send own public key
                await PacketHelper.SendAsync(stream, new NetworkPacket
                {
                    Type = PacketType.KeyExchange,
                    SenderId = _ownId,
                    Payload = _crypto.PublicKeyXml,
                    Timestamp = DateTime.UtcNow
                }, ct);
                _logger.LogDebug("Sent own public key");

                // 2. Receive peer's public key
                var kePkt = await PacketHelper.ReceiveAsync(stream, ct);
                if (kePkt?.Type != PacketType.KeyExchange)
                {
                    _logger.LogWarning("Handshake abort: expected KeyExchange, got {PacketType}", kePkt?.Type.ToString() ?? "null");
                    return;
                }
                contactId = kePkt.SenderId;
                _logger.LogDebug("Got peer key, contactId={ContactId}", Short(contactId));

                // 3. Receive encrypted AES session key
                var skPkt = await PacketHelper.ReceiveAsync(stream, ct);
                if (skPkt?.Type != PacketType.SessionKey)
                {
                    _logger.LogWarning("Handshake abort: expected SessionKey, got {PacketType}", skPkt?.Type.ToString() ?? "null");
                    return;
                }
                _crypto.DecryptAndStoreSessionKey(contactId, skPkt.Payload);
                _logger.LogInformation("Handshake complete with {ContactId}", Short(contactId));

                lock (_streamsLock) _activeStreams[contactId] = stream;
                ContactConnected?.Invoke(this, new ContactStatusEventArgs(contactId, isOnline: true));

                // 4. Receive loop
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    var pkt = await PacketHelper.ReceiveAsync(stream, ct);
                    if (pkt == null) { _logger.LogInformation("Peer closed stream ({ContactId})", Short(contactId)); break; }
                    await ProcessPacketAsync(pkt, stream, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Client handler error ({ContactId})", Short(contactId ?? "?"));
            }
            finally
            {
                client.Close();
                if (contactId != null)
                {
                    lock (_streamsLock) _activeStreams.Remove(contactId);
                    _crypto.RemoveSession(contactId);
                    ContactDisconnected?.Invoke(this, new ContactStatusEventArgs(contactId, isOnline: false));
                    _logger.LogInformation("Disconnected {ContactId}", Short(contactId));
                }
            }
        }

        // ── Packet dispatch ───────────────────────────────────────────────

        private async Task ProcessPacketAsync(NetworkPacket pkt, NetworkStream stream, CancellationToken ct)
        {
            switch (pkt.Type)
            {
                case PacketType.Message:
                    var plain = _crypto.DecryptMessage(pkt.Payload, pkt.SenderId);
                    _logger.LogInformation("Message received from {ContactId} id={MessageId}", Short(pkt.SenderId), pkt.MessageId);
                    MessageReceived?.Invoke(this, new MessageReceivedEventArgs(
                        pkt.SenderId, plain, pkt.MessageId ?? string.Empty, pkt.Timestamp));

                    await PacketHelper.SendAsync(stream, new NetworkPacket
                    {
                        Type = PacketType.DeliveryAck,
                        SenderId = _ownId,
                        MessageId = pkt.MessageId,
                        Timestamp = DateTime.UtcNow
                    }, ct);
                    break;

                case PacketType.DeliveryAck:
                case PacketType.ReadAck:
                    if (pkt.MessageId != null)
                        DeliveryAcknowledged?.Invoke(this, new DeliveryAckEventArgs(pkt.MessageId));
                    break;

                case PacketType.Disconnect:
                    break;
            }
        }

        // ── Outbound read-receipt ─────────────────────────────────────────

        public async Task SendReadAckAsync(string contactId, string messageId)
        {
            NetworkStream? stream;
            lock (_streamsLock) _activeStreams.TryGetValue(contactId, out stream);
            if (stream == null) return;

            try
            {
                await PacketHelper.SendAsync(stream, new NetworkPacket
                {
                    Type = PacketType.ReadAck,
                    SenderId = _ownId,
                    MessageId = messageId,
                    Timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Read-ack send failed for {ContactId}", Short(contactId));
            }
        }

        private static string Short(string id) => id.Length > 8 ? id[..8] : id;

        public void Dispose() => Stop();
    }
}