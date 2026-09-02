using System.Windows;
using EncryptedMessenger.Core.IPC;
using EncryptedMessenger.Core.Models;
using EncryptedMessenger.Core.Services;
using EncryptedMessenger.WPF.ViewModels;

namespace EncryptedMessenger.WPF
{
    public partial class App : Application
    {
        public static AppSettings   Settings { get; private set; } = null!;
        public static MainViewModel MainVM   { get; private set; } = null!;
        public static PipeClient    Pipe     { get; private set; } = null!;

        // Falls kein Windows-Dienst läuft, startet die App den Service selbst
        private static MessengerService? _standaloneService;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Settings = AppSettings.Load();
            Pipe     = new PipeClient();
            MainVM   = new MainViewModel(Settings, Pipe);
            // MainVM ruft intern Pipe.StartAsync() auf

            // 2 Sekunden warten ob sich der Windows-Dienst meldet
            bool serviceRunning = await WaitForServiceAsync(timeoutMs: 2000);
            if (!serviceRunning)
            {
                // Standalone-Modus: Dienst direkt in der WPF-App starten
                _standaloneService = new MessengerService(Settings);
                await _standaloneService.StartAsync();
            }
        }

        private static async Task<bool> WaitForServiceAsync(int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (Pipe.IsConnected) return true;
                await Task.Delay(100);
            }
            return false;
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            MainVM.Dispose();
            if (_standaloneService != null)
                await _standaloneService.DisposeAsync();
            base.OnExit(e);
        }
    }
}
