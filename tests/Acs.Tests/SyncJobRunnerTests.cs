using Acs.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acs.Tests;

/// <summary>Běh dlouhých synchronizací na pozadí (import z AD u velkých domén).</summary>
public class SyncJobRunnerTests
{
    private static SyncJobRunner Create()
        => new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SyncJobRunner>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    [Fact]
    public async Task Start_RunsWorkInBackgroundAndRecordsResult()
    {
        var runner = Create();
        Assert.True(runner.Start("import", (_, _) => Task.FromResult("hotovo: 4965 zaměstnanců")));

        await WaitUntilAsync(() => runner.Get("import")?.Running == false);

        var status = runner.Get("import");
        Assert.NotNull(status);
        Assert.False(status.Running);
        Assert.Equal("hotovo: 4965 zaměstnanců", status.Result);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task Start_CapturesFailureInsteadOfCrashing()
    {
        var runner = Create();
        runner.Start("import", (_, _) => throw new InvalidOperationException("LDAP timeout"));

        await WaitUntilAsync(() => runner.Get("import")?.Running == false);

        var status = runner.Get("import");
        Assert.NotNull(status);
        Assert.Equal("LDAP timeout", status.Error);
        Assert.Null(status.Result);
    }

    [Fact]
    public async Task Start_RefusesSecondRunWhileFirstIsRunning()
    {
        var runner = Create();
        var release = new TaskCompletionSource();
        Assert.True(runner.Start("import", async (_, _) => { await release.Task; return "ok"; }));

        await WaitUntilAsync(() => runner.IsRunning("import"));
        Assert.False(runner.Start("import", (_, _) => Task.FromResult("druhý")));

        release.SetResult();
        await WaitUntilAsync(() => runner.Get("import")?.Running == false);
        Assert.Equal("ok", runner.Get("import")!.Result);

        // Po dokončení jde spustit znovu.
        Assert.True(runner.Start("import", (_, _) => Task.FromResult("znovu")));
    }

    [Fact]
    public void UnknownJob_HasNoStatus()
    {
        var runner = Create();
        Assert.Null(runner.Get("neexistuje"));
        Assert.False(runner.IsRunning("neexistuje"));
    }
}
