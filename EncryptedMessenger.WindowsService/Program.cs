using EncryptedMessenger.WindowsService;

var builder = Host.CreateApplicationBuilder(args);

// Register as a Windows Service (ignored when run interactively / in debug)
builder.Services.AddWindowsService(options =>
    options.ServiceName = "EncryptedMessenger");

builder.Services.AddHostedService<MessengerWorker>();

builder.Logging.AddEventLog(settings =>
    settings.SourceName = "EncryptedMessenger");

var host = builder.Build();
host.Run();
