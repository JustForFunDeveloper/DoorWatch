using System.Text.Json.Serialization;
using DoorWatch.Camera;
using DoorWatch.Core;
using DoorWatch.HomeAssistant;
using DoorWatch.Worker;
using Microsoft.Extensions.Logging.Console;

var builder = WebApplication.CreateBuilder(args);

// Listen on a fixed container-internal port for the diagnostics endpoints; map it to the host in compose.
builder.WebHost.UseUrls("http://0.0.0.0:8080");

// Replace the default console provider with our single-line pipe formatter (avoids double logging).
builder.Logging.ClearProviders();
builder.Logging
    .AddConsole(o => o.FormatterName = PipeFormatter.Name)
    .AddConsoleFormatter<PipeFormatter, SimpleConsoleFormatterOptions>();
// Keep ASP.NET's own request/hosting chatter out of the detection log.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

// Serialize enums as their names so /status is human-readable.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var cfg = builder.Configuration.GetSection("DoorWatch");

builder.Services.AddSingleton(cfg.GetSection("Camera").Get<CameraConfig>() ?? new CameraConfig());
builder.Services.AddSingleton(cfg.GetSection("Detector").Get<DetectorConfig>() ?? new DetectorConfig());
builder.Services.AddSingleton(cfg.GetSection("HomeAssistant").Get<HomeAssistantConfig>() ?? new HomeAssistantConfig());

builder.Services.AddSingleton<DetectionStatus>();
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

var app = builder.Build();

// On-demand snapshot of what the detector currently sees — scores, thresholds in effect,
// frame freshness, and connection health. Read it with: curl http://<host>:8088/status
app.MapGet("/status", (DetectionStatus status) => Results.Json(status.Snapshot()));

// Liveness/readiness: healthy only when a recent frame was grabbed. A stale frame age (or no
// frame at all) returns 503, which distinguishes a frozen feed from a wrong-but-live reading.
app.MapGet("/healthz", (DetectionStatus status, DetectorConfig detectorConfig) =>
{
    var snap = status.Snapshot();
    bool healthy = snap.FrameAgeMs is { } age && age < detectorConfig.StaleFrameSeconds * 1000;
    return healthy ? Results.Ok(snap) : Results.Json(snap, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();
