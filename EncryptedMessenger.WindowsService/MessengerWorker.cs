using EncryptedMessenger.Core.Models;
using EncryptedMessenger.Core.Services;

namespace EncryptedMessenger.WindowsService
{
    /// <summary>
    /// Long-running Worker registered as a Windows Service.
    ///
    /// Install:
    ///   sc create EncryptedMessenger binPath= "C:\...\EncryptedMessenger.Service.exe"
    ///   sc start  EncryptedMessenger
    ///
    /// Or via the .NET tool:
    ///   dotnet publish -r win-x64 -c Release
    ///   sc create EncryptedMessenger binPath= "publish\EncryptedMessenger.Service.exe"
    /// </summary>
    public sealed class MessengerWorker : BackgroundService
    {
        private readonly ILogger<MessengerWorker> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private MessengerService? _service;

        public MessengerWorker(ILogger<MessengerWorker> logger, ILoggerFactory loggerFactory)
        {
            _logger = logger;
            _loggerFactory = loggerFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("EncryptedMessenger Service starting…");

            var settings = AppSettings.Load();
            _service     = new MessengerService(settings, _loggerFactory);

            try
            {
                await _service.StartAsync();
                _logger.LogInformation(
                    "Service running on TCP:{TcpPort} UDP:{UdpPort}",
                    settings.TcpPort, settings.UdpPort);

                // Keep the worker alive until the host requests cancellation
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Fatal error in MessengerService.");
                // Rethrow: the .NET host then stops (BackgroundServiceExceptionBehavior.StopHost)
                // and the service ends as failed. Windows only restarts it if recovery actions
                // are configured, e.g. sc failure EncryptedMessenger reset= 86400 actions= restart/5000
                throw;
            }
            finally
            {
                if (_service != null)
                    await _service.DisposeAsync();

                _logger.LogInformation("EncryptedMessenger Service stopped.");
            }
        }
    }
}
