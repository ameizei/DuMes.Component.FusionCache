using DuMes.Component.FusionCache.DependencyInjection;
using DuMes.Component.Serilog.DependencyInjection;
using DuMes.Component.Serilog.Logging;
using Serilog;
using TestWebApi.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseComponentSerilog();
builder.Services.AddComponentFusionCache(builder.Configuration);
builder.Services.AddOpenApi();

LogFile.ClearFixedName("cache");
LogFile.ClearFixedName("redis");

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapFusionCacheEndpoints();

try
{
    app.Logger.LogInformation("TestWebApi 已启动，演示接口见 GET /cache/");
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}
