using DuMes.Component.FusionCache.DependencyInjection;
using TestWorkerService.Cluster;
using TestWorkerService.Shared;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddComponentFusionCache(builder.Configuration);
builder.Services.AddSingleton(sp =>
    new FusionCacheScenarioRunner(
        builder.Configuration,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("FusionCacheScenarios"),
        modeLabel: "Cluster"));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
