using DoorWatch.Camera;
using DoorWatch.Core;
using DoorWatch.HomeAssistant;
using DoorWatch.Worker;
using Microsoft.Extensions.Logging.Console;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging
    .AddConsole(o => o.FormatterName = PipeFormatter.Name)
    .AddConsoleFormatter<PipeFormatter, SimpleConsoleFormatterOptions>();

var cfg = builder.Configuration.GetSection("DoorWatch");

builder.Services.AddSingleton(cfg.GetSection("Camera").Get<CameraConfig>() ?? new CameraConfig());
builder.Services.AddSingleton(cfg.GetSection("Detector").Get<DetectorConfig>() ?? new DetectorConfig());
builder.Services.AddSingleton(cfg.GetSection("HomeAssistant").Get<HomeAssistantConfig>() ?? new HomeAssistantConfig());

builder.Services.AddSingleton<IDoorDetector, PixelDifferenceDetector>();

builder.Services.AddHttpClient<IHomeAssistantClient, HomeAssistantClient>((sp, client) =>
{
    var haCfg = sp.GetRequiredService<HomeAssistantConfig>();
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", haCfg.Token);
});

if (args.Contains("--snapshot"))
    builder.Services.AddHostedService<SnapshotWorker>();
else
    builder.Services.AddHostedService<DoorWatchWorker>();

var host = builder.Build();
host.Run();
