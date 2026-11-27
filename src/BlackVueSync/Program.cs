using BlackVueSync;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

builder.Services.AddOptions<SyncOptions>()
    .Bind(builder.Configuration.GetSection("BlackVue"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Host), "BlackVue__Host must be set to the dashcam's IP address")
    .Validate(o => o.PollIntervalSeconds >= 5, "BlackVue__PollIntervalSeconds must be at least 5")
    .Validate(o => o.MaxParallelDownloads is >= 1 and <= 8, "BlackVue__MaxParallelDownloads must be between 1 and 8")
    .ValidateOnStart();

builder.Services.AddSingleton<BlackVueClient>();
builder.Services.AddHostedService<SyncWorker>();

await builder.Build().RunAsync();
