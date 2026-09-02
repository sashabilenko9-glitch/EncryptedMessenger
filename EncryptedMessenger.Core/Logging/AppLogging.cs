using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace EncryptedMessenger.Core.Logging
{
    /// <summary>
    /// Builds the shared file-based <see cref="ILoggerFactory"/> used by both the
    /// WPF app (standalone mode) and the Windows Service, so the network/crypto
    /// layer in Core has real, persisted diagnostics instead of Debug.WriteLine
    /// output that disappears outside an attached debugger.
    /// </summary>
    public static class AppLogging
    {
        public const string LogDirectory = @".\data\logs";

        public static ILoggerFactory CreateLoggerFactory(string? logDirectory = null)
        {
            logDirectory ??= LogDirectory;
            Directory.CreateDirectory(logDirectory);

            var serilogLogger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    Path.Combine(logDirectory, "log-.txt"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    restrictedToMinimumLevel: LogEventLevel.Debug,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            return LoggerFactory.Create(builder => builder.AddSerilog(serilogLogger, dispose: true));
        }
    }
}
