using EncryptedMessenger.Core.Models;
using EncryptedMessenger.Core.Services;

namespace EncryptedMessenger.Tests
{
    public class AppSettingsTests : IDisposable
    {
        private readonly TempDir _dir = new();
        public void Dispose() => _dir.Dispose();

        [Fact]
        public void FirstLoad_SavesSettings_SoUserIdStaysStable()
        {
            var path = _dir.File("settings.json");

            var first = AppSettings.Load(path);
            var second = AppSettings.Load(path);

            Assert.True(File.Exists(path));
            Assert.Equal(first.UserId, second.UserId);
        }

        [Fact]
        public void CorruptFile_IsBackedUp_AndReplacedWithPersistedDefaults()
        {
            var path = _dir.File("settings.json");
            File.WriteAllText(path, "{not json");

            var loaded = AppSettings.Load(path);

            Assert.Equal("{not json", File.ReadAllText(path + ".corrupt"));
            Assert.Equal(loaded.UserId, AppSettings.Load(path).UserId);
        }
    }

    public class PresenceTrackerTests
    {
        [Fact]
        public async Task Heard_IsOnline_UntilTimeout()
        {
            var p = new PresenceTracker(TimeSpan.FromMilliseconds(200));
            p.Heard("a");

            Assert.True(p.TryChangeReported("a", out var online) && online);
            Assert.False(p.TryChangeReported("a", out _));   // no change → nothing to broadcast

            await Task.Delay(300);
            Assert.True(p.TryChangeReported("a", out var stillOnline) && !stillOnline);
        }

        [Fact]
        public void Connections_AreRefCounted()
        {
            var p = new PresenceTracker(TimeSpan.FromSeconds(10));
            p.ConnectionOpened("b");
            p.ConnectionOpened("b");

            p.ConnectionClosed("b");
            Assert.True(p.IsOnline("b"));

            p.ConnectionClosed("b");
            Assert.False(p.IsOnline("b"));
        }
    }
}
