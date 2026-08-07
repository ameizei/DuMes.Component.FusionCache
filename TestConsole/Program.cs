using DuMes.Component.FusionCache.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestWorkerService.Shared;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddComponentFusionCache(builder.Configuration);

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("FusionCacheScenarios");

var runner = new FusionCacheScenarioRunner(builder.Configuration, logger, modeLabel: "Standalone");
var results = await runner.RunAllAsync(CancellationToken.None);

var exitCode = results.All(r => r.Passed) ? 0 : 1;
Console.WriteLine($"单机控制台场景完成，退出码={exitCode}");
Environment.Exit(exitCode);
