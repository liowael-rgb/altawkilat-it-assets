using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Itam.App;

/// <summary>Mail settings of the host: a test recipient receives every email instead of the real recipients (test phase).</summary>
public sealed record ItamMailOptions(string? TestRecipient);

/// <summary>Every minute, sends the queued emails through the mail service, after the actions committed; every hour, writes the
/// reminders and the weekly emails (each once per period). Without a mail service the emails stay queued in the outbox.</summary>
public sealed class ItamMailSender(IServiceScopeFactory scopes, ILogger<ItamMailSender> log) : BackgroundService
{
    static readonly byte[] Logo = ReadLogo();
    static byte[] ReadLogo()
    {
        using var s = typeof(ItamMailSender).Assembly.GetManifestResourceStream("Itam.App.Assets.company-logo.png");
        if (s is null) return [];
        using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        var lastJobs = DateTime.MinValue;
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ItamStore>();
                if (DateTime.UtcNow - lastJobs >= TimeSpan.FromHours(1)) { var n = await store.RunMailJobs(null, token); lastJobs = DateTime.UtcNow; if (n > 0) log.LogInformation("IT Assets: {Count} reminder email(s) queued.", n); }
                if (scope.ServiceProvider.GetService<IItamMailer>() is { } mailer)
                    await store.SendQueuedMail(mailer, scope.ServiceProvider.GetService<ItamMailOptions>()?.TestRecipient, Logo, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "IT Assets: the mail sender failed; it runs again in a minute."); }
        } while (await timer.WaitForNextTickAsync(token));
    }
}
