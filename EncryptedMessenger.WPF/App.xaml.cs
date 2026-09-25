using System.Windows;
using EncryptedMessenger.Core.IPC;
using EncryptedMessenger.Core.Logging;
using EncryptedMessenger.Core.Models;
using EncryptedMessenger.Core.Services;
using EncryptedMessenger.WPF.ViewModels;
using Microsoft.Extensions.Logging;

namespace EncryptedMessenger.WPF
{
    public partial class App : Application
    {
        public static AppSettings    Settings      { get; private set; } = null!;
        public static MainViewModel  MainVM        { get; private set; } = null!;
        public static PipeClient     Pipe          { get; private set; } = null!;
        public static ILoggerFactory LoggerFactory { get; private set; } = null!;

        // If no Windows Service is running, the app starts the service itself
        private static MessengerService? _standaloneService;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // All data paths are relative (.\data\...). Anchor them to the exe folder, not to
            // whatever working directory a shortcut or terminal happened to launch us from.
            System.IO.Directory.SetCurrentDirectory(AppContext.BaseDirectory);

            LoggerFactory = AppLogging.CreateLoggerFactory();
            var logger = LoggerFactory.CreateLogger<App>();

            Settings = AppSettings.Load();
            Pipe     = new PipeClient(LoggerFactory.CreateLogger<PipeClient>());
            MainVM   = new MainViewModel(Settings, Pipe, LoggerFactory);
            // MainVM internally calls Pipe.StartAsync()

            // Wait 2 seconds to see if the Windows Service responds
            bool serviceRunning = await WaitForServiceAsync(timeoutMs: 2000);
            if (!serviceRunning)
            {
                // Standalone mode: start the service directly inside the WPF app
                logger.LogInformation("No background service detected, starting standalone");
                _standaloneService = new MessengerService(Settings, LoggerFactory);
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
            LoggerFactory.Dispose();
            base.OnExit(e);
        }
    }
}
