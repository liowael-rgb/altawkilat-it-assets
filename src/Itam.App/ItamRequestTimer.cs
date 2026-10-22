using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Itam.App;

/// <summary>Every 15 minutes, cancels the requests whose manager did not decide within the approval time (3 days). Pages and
/// decisions also check first, so an overdue request is never approved late even between runs.</summary>
public sealed class ItamRequestTimer(IServiceScopeFactory scopes, ILogger<ItamRequestTimer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var n = await scope.ServiceProvider.GetRequiredService<ItamStore>().ExpireRequests(token);
                if (n > 0) log.LogInformation("IT Assets: {Count} request(s) cancelled, not approved in time.", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "IT Assets: the request timer failed; it runs again in 15 minutes."); }
        } while (await timer.WaitForNextTickAsync(token));
    }
}
