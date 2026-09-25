using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EncryptedMessenger.Core.Encryption;
using EncryptedMessenger.Core.IPC;
using EncryptedMessenger.Core.Models;
using EncryptedMessenger.Core.Services;
using Microsoft.Data.Sqlite;

// Several tests change the process-wide working directory (MessengerService uses relative
// .\data paths) and open real sockets — run test classes one after another, not in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace EncryptedMessenger.Tests
{
    /// <summary>A throw-away directory, deleted (best effort) when the test ends.</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "emtests_" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public CryptoManager NewKeys(string name) => new(File(name + ".key"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();   // pooled connections keep the .db file locked
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static class Net
    {
        /// <summary>A TCP port that is free right now (the OS picks it).</summary>
        public static int FreeTcpPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public static int FreeUdpPort()
        {
            using var u = new UdpClient(0);
            return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
        }
    }

    public static class Wait
    {
        /// <summary>Polls until <paramref name="condition"/> holds or the timeout passes; returns the final result.</summary>
        public static async Task<bool> Until(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(25);
            }
            return condition();
        }
    }

    /// <summary>
    /// A real <see cref="MessengerService"/> ("Bob") in its own temp directory, with a unique
    /// pipe name and free ports, plus a <see cref="PipeClient"/> playing Bob's UI that records
    /// every message the service broadcasts.
    /// </summary>
    public sealed class ServiceHarness : IAsyncDisposable
    {
        private readonly string _previousDirectory = Directory.GetCurrentDirectory();

        public TempDir Dir { get; } = new();
        public AppSettings Settings { get; }
        public MessengerService Service { get; }
        public PipeClient Ui { get; }
        public ConcurrentQueue<PipeMessage> Events { get; } = new();
        public string BobId => Settings.UserId;
        public int TcpPort => Settings.TcpPort;
        public int UdpPort => Settings.UdpPort;

        private ServiceHarness(bool discovery)
        {
            Directory.SetCurrentDirectory(Dir.Path);   // the service's .\data lives here
            Settings = new AppSettings
            {
                TcpPort = Net.FreeTcpPort(),
                UdpPort = Net.FreeUdpPort(),
                Discovery = discovery,
                DisplayName = "Bob"
            };
            var pipeName = "EncryptedMessengerTests_" + Guid.NewGuid().ToString("N");
            Service = new MessengerService(Settings, pipeName: pipeName);
            Ui = new PipeClient(pipeName: pipeName);
            Ui.MessageReceived += (_, m) => Events.Enqueue(m);
        }

        public static async Task<ServiceHarness> StartAsync(bool discovery = false)
        {
            var h = new ServiceHarness(discovery);
            await h.Service.StartAsync();
            _ = h.Ui.StartAsync();
            Assert.True(await Wait.Until(() => h.Ui.IsConnected), "UI pipe client did not connect");
            await Task.Delay(200);   // let the TCP listener come up
            return h;
        }

        /// <summary>Bob's own public key (same DPAPI-protected file, same Windows user).</summary>
        public string BobPublicKey => new CryptoManager(AppSettings.PrivateKeyFile).PublicKeyXml;

        public Task SendUi<T>(PipeMessageType type, T payload) => Ui.SendAsync(PipeMessage.Create(type, payload));

        public T? Last<T>(PipeMessageType type) where T : class
            => Events.Where(m => m.Type == type).Select(m => m.Deserialize<T>()).LastOrDefault();

        public List<Contact> Contacts() => Last<List<Contact>>(PipeMessageType.ContactList) ?? [];
        public List<ContactRequestPayload> Requests() => Last<List<ContactRequestPayload>>(PipeMessageType.RequestList) ?? [];

        public async ValueTask DisposeAsync()
        {
            Ui.Stop();
            await Service.DisposeAsync();
            Directory.SetCurrentDirectory(_previousDirectory);
            Dir.Dispose();
        }
    }
}
