using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Itam.App;

/// <summary>One run of the AD employee job: when, how it was started (schedule or the person), what changed, or the error.</summary>
public sealed record ItamDirectorySyncRun(DateTime StartedUtc, DateTime FinishedUtc, string StartedBy, bool Ok, int ReadAccounts, int Added, int Changed, int Disabled, string? Error);

/// <summary>The AD employee job (owner, 22 October 2026: every 15 minutes, or now from the Administration Center). It copies the
/// users OU into the employee list (names, departments, managers, enabled). A read that would disable more than half of the
/// enabled employees is refused (a wrong OU or a partial read) and nothing changes. One run at a time.</summary>
public sealed class ItamDirectorySync(IServiceScopeFactory scopes, Func<CancellationToken, Task<IReadOnlyCollection<ItamEmployee>>> read, int everyMinutes, ILogger<ItamDirectorySync> log, TimeProvider? time = null)
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly TimeProvider clock = time ?? TimeProvider.System;
    public int EveryMinutes { get; } = everyMinutes;
    public ItamDirectorySyncRun? Last { get; private set; }
    public bool Running => gate.CurrentCount == 0;

    /// <summary>Runs the job now; null when a run is already going on.</summary>
    public async Task<ItamDirectorySyncRun?> Run(string startedBy, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) return null;
        var started = clock.GetUtcNow().UtcDateTime;
        try
        {
            ItamDirectorySyncRun run;
            try
            {
                var people = await read(token);
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ItamStore>();
                var enabledNow = await store.EnabledEmployees(token);
                var enabledRead = people.Count(p => p.Enabled);
                if (people.Count == 0 || (enabledNow > 20 && enabledRead < enabledNow / 2))
                {
                    var why = $"AD returned {enabledRead} enabled accounts for {enabledNow} enabled employees: nothing changed (check the users OU).";
                    log.LogError("IT Assets: {Why}", why);
                    run = new(started, clock.GetUtcNow().UtcDateTime, startedBy, false, people.Count, 0, 0, 0, why);
                }
                else
                {
                    var (added, changed, disabled) = await store.SyncEmployees(people, token);
                    log.LogInformation("IT Assets: employees from AD ({By}): {Added} added, {Changed} changed, {Disabled} disabled.", startedBy, added, changed, disabled);
                    run = new(started, clock.GetUtcNow().UtcDateTime, startedBy, true, people.Count, added, changed, disabled, null);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "IT Assets: the AD employee read failed ({By}); it runs again later.", startedBy);
                run = new(started, clock.GetUtcNow().UtcDateTime, startedBy, false, 0, 0, 0, 0, "Active Directory could not be read: " + ex.Message);
            }
            Last = run;
            return run;
        }
        finally { gate.Release(); }
    }
}

/// <summary>Runs the AD employee job at start and then every EveryMinutes minutes.</summary>
public sealed class ItamDirectorySyncJob(ItamDirectorySync sync) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(sync.EveryMinutes));
        do await sync.Run("Schedule", token);
        while (await timer.WaitForNextTickAsync(token));
    }
}

public static class ItamDirectorySyncServices
{
    /// <summary>The AD employee job: read = the users OU (production: LDAPS), every = minutes between runs (15 to 1440).</summary>
    public static IServiceCollection AddItamDirectorySync(this IServiceCollection services, Func<IServiceProvider, Func<CancellationToken, Task<IReadOnlyCollection<ItamEmployee>>>> read, int everyMinutes, bool schedule = true)
    {
        if (everyMinutes is < 15 or > 1440) throw new ArgumentOutOfRangeException(nameof(everyMinutes), "The AD job runs every 15 to 1440 minutes.");
        services.AddSingleton(sp => new ItamDirectorySync(sp.GetRequiredService<IServiceScopeFactory>(), read(sp), everyMinutes, sp.GetRequiredService<ILogger<ItamDirectorySync>>(), sp.GetService<TimeProvider>()));
        if (schedule) services.AddHostedService<ItamDirectorySyncJob>();
        return services;
    }
}

public sealed partial class ItamStore
{
    static object RunView(ItamDirectorySyncRun r) => new { started = r.StartedUtc, finished = r.FinishedUtc, by = r.StartedBy, ok = r.Ok, read = r.ReadAccounts, added = r.Added, changed = r.Changed, disabled = r.Disabled, error = r.Error };

    /// <summary>The AD employee job for the Administration Center: how often, the last run, and whether this person may run it now.</summary>
    public async Task<object> DirectorySyncStatus(ItamUser user, ItamDirectorySync? sync, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.MasterData || s.RolesManage || s.AuditView, "The Administration Center is for the IT Manager.");
        return new { available = sync is not null, everyMinutes = sync?.EveryMinutes, running = sync?.Running ?? false, canRun = sync is not null && (s.Manager || s.SysAdmin),
            last = sync?.Last is { } r ? RunView(r) : null };
    }

    /// <summary>Runs the AD employee job now (IT Manager or System Administrator); kept in the audit log.</summary>
    public async Task<object> RunDirectorySync(ItamUser user, ItamDirectorySync? sync, CancellationToken token)
    {
        string name;
        await using (var c = await Open(token))
        {
            var s = await Scope(c, null, user, token);
            Require(s.Manager || s.SysAdmin, "Only the IT Manager and the System Administrator run the AD job.");
            name = s.Name;
        }
        if (sync is null) throw new ArgumentException("Active Directory is not set up on this site.");
        var run = await sync.Run(name, token) ?? throw new ItamConflict("The AD job is running now; wait a minute and look again.");
        await using (var c = await Open(token))
        {
            await using var tx = await c.BeginTransactionAsync(token);
            await Audit(c, tx, user.Id, "ad-sync", run.StartedUtc.ToString("yyyy-MM-dd HH:mm:ss"), "Run now", null, new { run.Ok, run.ReadAccounts, run.Added, run.Changed, run.Disabled, run.Error }, null, token);
            await tx.CommitAsync(token);
        }
        return RunView(run);
    }
}
