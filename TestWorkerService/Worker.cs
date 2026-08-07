using TestWorkerService.Shared;

namespace TestWorkerService;

/// <summary>单机 Redis 场景：启动后跑完全部用例并退出。</summary>
public sealed class Worker(
    FusionCacheScenarioRunner runner,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 等宿主完全启动
        await Task.Yield();

        var exitCode = 1;
        try
        {
            var results = await runner.RunAllAsync(stoppingToken);
            exitCode = results.All(r => r.Passed) ? 0 : 1;
            logger.LogInformation("单机场景完成，退出码={ExitCode}", exitCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "单机场景执行异常");
            exitCode = 1;
        }
        finally
        {
            Environment.Exit(exitCode);
        }
    }
}
