using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EncryptedMessenger.Core.Models;

namespace EncryptedMessenger.Core.Network
{
    /// <summary>
    /// Wire protocol helpers.
    ///
    /// Frame format (TCP):
    ///   [4 bytes little-endian int32 = payload length] [payload bytes UTF-8 JSON]
    ///
    /// Max single packet = 1 MB (guards against runaway allocations).
    /// </summary>
    public static class PacketHelper
    {
        private const int MaxPacketBytes = 1_048_576; // 1 MB

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        // ── Write ─────────────────────────────────────────────────────────

        public static async Task SendAsync(NetworkStream stream, NetworkPacket packet, CancellationToken ct = default)
        {
            var json  = JsonSerializer.Serialize(packet, JsonOpts);
            var body  = Encoding.UTF8.GetBytes(json);
            var lenBuf = BitConverter.GetBytes(body.Length);   // 4 bytes LE

            await stream.WriteAsync(lenBuf, ct);
            await stream.WriteAsync(body,   ct);
            await stream.FlushAsync(ct);
        }

        // ── Read ──────────────────────────────────────────────────────────

        public static async Task<NetworkPacket?> ReceiveAsync(NetworkStream stream, CancellationToken ct = default)
        {
            // Read length prefix
            var lenBuf = new byte[4];
            if (!await ReadExactAsync(stream, lenBuf, ct)) return null;

            var length = BitConverter.ToInt32(lenBuf, 0);
            if (length <= 0 || length > MaxPacketBytes)
                throw new InvalidDataException($"Invalid packet length: {length}");

            // Read body
            var body = new byte[length];
            if (!await ReadExactAsync(stream, body, ct)) return null;

            var json = Encoding.UTF8.GetString(body);
            return JsonSerializer.Deserialize<NetworkPacket>(json, JsonOpts);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>Reads exactly <c>buffer.Length</c> bytes; returns false on clean EOF.</summary>
        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
                if (read == 0) return false; // connection closed
                offset += read;
            }
            return true;
        }
    }
}
