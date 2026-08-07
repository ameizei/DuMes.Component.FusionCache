namespace TestWorkerService.Shared;

/// <summary>单条场景执行结果。</summary>
public sealed record ScenarioResult(string Name, bool Passed, string Detail, long ElapsedMs)
{
    public override string ToString() =>
        $"[{(Passed ? "PASS" : "FAIL")}] {Name} ({ElapsedMs}ms) — {Detail}";
}
