using System.Text.Json;

namespace EncryptedMessenger.Core.Models
{
    /// <summary>
    /// Persisted application settings (JSON file next to the executable).
    /// All paths are relative so the app is portable.
    /// </summary>
    public class AppSettings
    {
        public const int DefaultTcpPort       = 9876;
        public const int DefaultUdpPort       = 9877;
        public const string SettingsFileName  = @".\data\settings.json";
        public const string DatabaseFileName  = @".\data\messenger.db";
        public const string PrivateKeyFile    = @".\data\private.key";
        public const string StorageKeyFile    = @".\data\storage.key";

        public string UserId      { get; set; } = Guid.NewGuid().ToString();
        public string DisplayName { get; set; } = Environment.UserName;
        public int    TcpPort     { get; set; } = DefaultTcpPort;
        public int    UdpPort     { get; set; } = DefaultUdpPort;
        public bool   AutoAccept  { get; set; } = true;
        public bool   Discovery   { get; set; } = true;

        // ── Persistence ───────────────────────────────────────────────────

        public void Save(string path = SettingsFileName)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }

        public static AppSettings Load(string path = SettingsFileName)
        {
            if (!File.Exists(path)) return new AppSettings();
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch { return new AppSettings(); }
        }
    }
}
