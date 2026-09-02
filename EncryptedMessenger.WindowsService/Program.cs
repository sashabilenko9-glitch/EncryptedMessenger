using EncryptedMessenger.Core.Logging;
using EncryptedMessenger.WindowsService;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Register as a Windows Service (ignored when run interactively / in debug)
builder.Services.AddWindowsService(options =>
    options.ServiceName = "EncryptedMessenger");

builder.Services.AddHostedService<MessengerWorker>();

builder.Logging.AddEventLog(settings =>
    settings.SourceName = "EncryptedMessenger");

// Rolling file log, same sink AppLogging.CreateLoggerFactory() uses in the WPF app,
// so both entry points end up writing to the same ./data/logs/ directory.
System.IO.Directory.CreateDirectory(AppLogging.LogDirectory);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.File(
        System.IO.Path.Combine(AppLogging.LogDirectory, "log-.txt"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();
builder.Logging.AddSerilog(dispose: true);

var host = builder.Build();
host.Run();
