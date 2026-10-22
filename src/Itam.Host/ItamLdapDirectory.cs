using System.DirectoryServices.Protocols;
using System.Net;
using System.Text.RegularExpressions;
using Itam.App;

namespace Itam.Host;

/// <summary>Reads Active Directory as VTMS does: LDAPS (port 636), Negotiate as the site's own Windows identity, paged, read only.
/// Users of the users OU (with sub-OUs) become the employee copy; computer accounts feed the AD comparison. Nothing is
/// ever written to AD.</summary>
public sealed class ItamLdapDirectory(DirectorySettings settings) : IItamComputerDirectory
{
    public string ComparedOus => string.Join("; ", settings.ComparedOus.Select(o => o.Dn + (o.SubOus ? " (with sub-OUs)" : "")));

    LdapConnection Connect()
    {
        var c = new LdapConnection(new LdapDirectoryIdentifier(settings.Host, 636)) { AuthType = AuthType.Negotiate, Credential = CredentialCache.DefaultNetworkCredentials, Timeout = TimeSpan.FromSeconds(30) };
        c.SessionOptions.ProtocolVersion = 3; c.SessionOptions.SecureSocketLayer = true; c.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        c.Bind();
        return c;
    }

    static IEnumerable<SearchResultEntry> Search(LdapConnection c, string baseDn, string filter, string[] attributes)
    {
        var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, attributes);
        var page = new PageResultRequestControl(500); request.Controls.Add(page);
        var count = 0;
        do
        {
            var response = (SearchResponse)c.SendRequest(request);
            foreach (SearchResultEntry e in response.Entries) { if (++count > 50_000) throw new InvalidOperationException("More than 50,000 directory entries: check the OU."); yield return e; }
            var paging = response.Controls.OfType<PageResultResponseControl>().SingleOrDefault() ?? throw new InvalidOperationException("The directory did not page the results; nothing is read partially.");
            page.Cookie = paging.Cookie;
        } while (page.Cookie.Length > 0);
    }

    static string? Text(SearchResultEntry e, string name) => e.Attributes.Contains(name) && e.Attributes[name].Count > 0 ? e.Attributes[name][0] as string ?? (e.Attributes[name][0] is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : e.Attributes[name][0]?.ToString()) : null;
    static Guid Guid(SearchResultEntry e) => e.Attributes.Contains("objectGUID") && e.Attributes["objectGUID"][0] is byte[] b && b.Length == 16 ? new Guid(b) : System.Guid.Empty;

    /// <summary>Every user of the users OU (enabled and disabled), with the manager linked by distinguished name.</summary>
    public IReadOnlyCollection<ItamEmployee> Employees()
    {
        using var c = Connect();
        var entries = Search(c, settings.UsersBaseDn, "(&(objectCategory=person)(objectClass=user))", ItamDirectoryMapping.UserAttributes).ToList();
        var byDn = entries.Where(e => Guid(e) != System.Guid.Empty).GroupBy(e => e.DistinguishedName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => Guid(g.First()), StringComparer.OrdinalIgnoreCase);
        return entries.Select(e => ItamDirectoryMapping.Employee(Guid(e), a => Text(e, a), dn => byDn.TryGetValue(dn, out var m) ? m : null)).OfType<ItamEmployee>().ToList();
    }

    public Task<IReadOnlyList<ItamAdComputer>> Computers(CancellationToken token)
    {
        using var c = Connect();
        var leaveOut = settings.LeaveOutNames.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).ToList();
        var ous = settings.ComparedOus.Select(o => (o.Dn, o.SubOus)).ToList();
        IReadOnlyList<ItamAdComputer> list = [.. Search(c, settings.DomainBaseDn, "(objectCategory=computer)", ItamDirectoryMapping.ComputerAttributes)
            .Select(e => ItamDirectoryMapping.Computer(a => Text(e, a), ous, leaveOut)).OfType<ItamAdComputer>()];
        return Task.FromResult(list);
    }
}

/// <summary>Copies the users OU into IT Assets every hour (names, departments, managers, enabled). A read that would disable more
/// than half of the enabled employees is refused (a wrong OU or a partial read), and nothing changes.</summary>
public sealed class ItamEmployeeSync(IServiceScopeFactory scopes, ItamLdapDirectory directory, DirectorySettings settings, ILogger<ItamEmployeeSync> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(settings.SyncEveryMinutes));
        do
        {
            try
            {
                var people = directory.Employees();
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ItamStore>();
                var enabledNow = await store.EnabledEmployees(token);
                if (enabledNow > 20 && people.Count(p => p.Enabled) < enabledNow / 2)
                    log.LogError("IT Assets: the AD read returned {Read} enabled users for {Now} enabled employees; nothing changed. Check ItamHost:Directory:UsersBaseDn.", people.Count(p => p.Enabled), enabledNow);
                else
                {
                    var (added, changed, disabled) = await store.SyncEmployees(people, token);
                    log.LogInformation("IT Assets: employees from AD: {Added} added, {Changed} changed, {Disabled} disabled.", added, changed, disabled);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "IT Assets: the AD employee read failed; it runs again later."); }
        } while (await timer.WaitForNextTickAsync(token));
    }
}

/// <summary>Runs the AD computer comparison once a day at the configured hour (Riyadh time).</summary>
public sealed class ItamAdCompareJob(IServiceScopeFactory scopes, ItamLdapDirectory directory, DirectorySettings settings, ILogger<ItamAdCompareJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        DateOnly? lastRun = null;
        while (await timer.WaitForNextTickAsync(token))
        {
            var now = DateTime.UtcNow.AddHours(3);
            if (now.Hour < settings.CompareAtHour || lastRun == DateOnly.FromDateTime(now)) continue;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ItamStore>().RunAdComparison(null, directory, token);
                lastRun = DateOnly.FromDateTime(now);
                log.LogInformation("IT Assets: daily AD computer comparison done.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "IT Assets: the AD computer comparison failed; it is tried again in 10 minutes."); }
        }
    }
}
