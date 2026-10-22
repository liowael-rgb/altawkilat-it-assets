using System.Data;

namespace Itam.App;

public sealed record ItamAdComputer(string Name, string Ou, bool Enabled, DateTime? LastLogonUtc, DateTime? CreatedUtc, string? Os, bool InComparedOus);
/// <summary>Reads the computer accounts from Active Directory with a read-only account; never changes AD.</summary>
public interface IItamComputerDirectory
{
    string ComparedOus { get; }
    Task<IReadOnlyList<ItamAdComputer>> Computers(CancellationToken token);
}
public sealed record ItamAdReview(int? LineId, string? Reason);

/// <summary>AD computer comparison (plan v1.9, form 34): the computer accounts of the chosen OUs against the computer names on
/// active delivery notes (Owner Type Other left out: those computers are not domain members). Names compare without case and
/// spaces. Results: Matched; In AD, no delivery note; Removal waiting (received back, the AD removal task still open); On a
/// delivery note, not in AD (with the OU when the name is elsewhere in the domain); Matched, needs a check (disabled, or no
/// sign-in for more than the inactive days). Every run is kept with its counts; a reviewed line stays hidden until the
/// computer changes. The System Administrator, the IT Assets Officer and the IT Manager run it and review lines; the Team
/// Leader sees all; IT Support Specialists see the lines of their regions. Licenses: who holds each software license.</summary>
public sealed partial class ItamStore
{
    public const int AdInactiveDays = 90;
    static string AdKey(string name) => new string(name.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
    static string AdResultLabel(string r) => r switch { "Matched" => "Matched", "NoDelivery" => "In AD, no delivery note", "RemovalWaiting" => "Removal waiting",
        "NotInAd" => "On a delivery note, not in AD", "NeedsCheck" => "Matched, needs a check", _ => r };
    static readonly string[] AdResults = ["NoDelivery", "RemovalWaiting", "NotInAd", "NeedsCheck", "Matched"];

    public async Task<object> RunAdComparison(ItamUser user, IItamComputerDirectory directory, CancellationToken token)
    {
        var computers = await directory.Computers(token);
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.AdCompareRun, "Only the System Administrator, the IT Assets Officer and the IT Manager run the AD comparison.");
        var deliveries = await Rows(c, tx, "SELECT d.delivery_id,d.computer_name,a.asset_id,g.region_code FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id JOIN itam_ref.location g ON g.location_id=a.location_id " +
            "WHERE d.received_at_utc IS NULL AND d.computer_name IS NOT NULL AND d.owner_type<>N'Other'", r => (Id: r.GetInt32(0), Name: r.GetString(1), Asset: r.GetInt32(2), Region: r.GetString(3)), token);
        var waiting = await Rows(c, tx, "SELECT d.delivery_id,d.computer_name,a.asset_id,g.region_code FROM itam_data.task k JOIN itam_data.delivery d ON d.delivery_id=k.delivery_id JOIN itam_data.asset a ON a.asset_id=k.asset_id " +
            "JOIN itam_ref.location g ON g.location_id=a.location_id WHERE k.kind=N'RemoveFromAd' AND k.done_at_utc IS NULL AND d.computer_name IS NOT NULL", r => (Id: r.GetInt32(0), Name: r.GetString(1), Asset: r.GetInt32(2), Region: r.GetString(3)), token);
        var reviews = (await Rows(c, tx, "SELECT computer_name,result,fingerprint FROM itam_data.ad_review", r => (r.GetString(0), r.GetString(1), r.GetString(2)), token)).ToHashSet();
        var byName = deliveries.GroupBy(d => AdKey(d.Name)).ToDictionary(g => g.Key, g => g.First());
        var waitingByName = waiting.GroupBy(d => AdKey(d.Name)).ToDictionary(g => g.Key, g => g.First());
        var inScope = computers.Where(x => x.InComparedOus).GroupBy(x => AdKey(x.Name)).ToDictionary(g => g.Key, g => g.First());
        var elsewhere = computers.Where(x => !x.InComparedOus).GroupBy(x => AdKey(x.Name)).ToDictionary(g => g.Key, g => g.First());
        var lines = new List<(string Result, string Name, ItamAdComputer? Ad, int? Delivery, int? Asset, string? Region)>();
        var limit = UtcNow.AddDays(-AdInactiveDays);
        foreach (var (key, ad) in inScope)
        {
            if (byName.TryGetValue(key, out var d)) lines.Add((!ad.Enabled || ad.LastLogonUtc is null || ad.LastLogonUtc < limit ? "NeedsCheck" : "Matched", ad.Name, ad, d.Id, d.Asset, d.Region));
            else if (waitingByName.TryGetValue(key, out var w)) lines.Add(("RemovalWaiting", ad.Name, ad, w.Id, w.Asset, w.Region));
            else lines.Add(("NoDelivery", ad.Name, ad, null, null, null));
        }
        foreach (var (key, d) in byName.Where(x => !inScope.ContainsKey(x.Key))) lines.Add(("NotInAd", d.Name, elsewhere.GetValueOrDefault(key), d.Id, d.Asset, d.Region));
        string Print(string result, string name, ItamAdComputer? ad, int? delivery) => $"{AdKey(name)}|{ad?.Ou}|{ad?.Enabled}|{delivery}";
        var runId = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.ad_run(run_by,ous,inactive_days,computers,matched,no_delivery,removal_waiting,not_in_ad,needs_check,reviewed) OUTPUT inserted.run_id " +
            "VALUES(@b,@o,@d,@c,@m,@n,@w,@x,@k,@r)", token, ("@b", s.Id), ("@o", directory.ComparedOus), ("@d", AdInactiveDays), ("@c", inScope.Count),
            ("@m", lines.Count(l => l.Result == "Matched")), ("@n", lines.Count(l => l.Result == "NoDelivery")), ("@w", lines.Count(l => l.Result == "RemovalWaiting")),
            ("@x", lines.Count(l => l.Result == "NotInAd")), ("@k", lines.Count(l => l.Result == "NeedsCheck")),
            ("@r", lines.Count(l => l.Result != "Matched" && reviews.Contains((AdKey(l.Name), l.Result, Print(l.Result, l.Name, l.Ad, l.Delivery)))))));
        foreach (var l in lines)
            await Exec(c, tx, "INSERT itam_data.ad_run_line(run_id,result,computer_name,ad_ou,ad_enabled,ad_last_logon_utc,ad_created_utc,ad_os,fingerprint,delivery_id,asset_id,region_code,reviewed) " +
                "VALUES(@r,@t,@n,@o,@e,@l,@c,@s,@f,@d,@a,@g,@v)", token, ("@r", runId), ("@t", l.Result), ("@n", l.Name), ("@o", l.Ad?.Ou), ("@e", l.Ad?.Enabled), ("@l", l.Ad?.LastLogonUtc),
                ("@c", l.Ad?.CreatedUtc), ("@s", l.Ad?.Os), ("@f", Print(l.Result, l.Name, l.Ad, l.Delivery)), ("@d", l.Delivery), ("@a", l.Asset), ("@g", l.Region),
                ("@v", l.Result != "Matched" && reviews.Contains((AdKey(l.Name), l.Result, Print(l.Result, l.Name, l.Ad, l.Delivery)))));
        await Audit(c, tx, s.Id, "adcompare", runId.ToString(), "Run", null, new { computers = inScope.Count, lines = lines.Count }, null, token);
        await tx.CommitAsync(token);
        return new { runId };
    }

    public async Task<object> AdComparison(ItamUser user, int? runId, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.AdCompareView, "You cannot see the AD comparison.");
        var runs = await Rows(c, null, "SELECT TOP 30 r.run_id,r.run_at_utc,e.display_name,r.ous,r.inactive_days,r.computers,r.matched,r.no_delivery,r.removal_waiting,r.not_in_ad,r.needs_check,r.reviewed " +
            "FROM itam_data.ad_run r LEFT JOIN itam_access.employee e ON e.employee_id=r.run_by ORDER BY r.run_id DESC",
            r => new { id = r.GetInt32(0), at = Utc(r, 1), by = S(r, 2), ous = r.GetString(3), inactiveDays = r.GetInt32(4), computers = r.GetInt32(5), matched = r.GetInt32(6), noDelivery = r.GetInt32(7),
                removalWaiting = r.GetInt32(8), notInAd = r.GetInt32(9), needsCheck = r.GetInt32(10), reviewed = r.GetInt32(11) }, token);
        var run = runId ?? runs.FirstOrDefault()?.id;
        var lines = run is null ? [] : (await Rows(c, null, "SELECT l.line_id,l.result,l.computer_name,l.ad_ou,l.ad_enabled,l.ad_last_logon_utc,l.ad_created_utc,l.ad_os,l.reviewed,d.delivery_id,d.delivery_no,d.delivered_on,d.owner_type," +
            "a.asset_no,e.display_name,e.account,e.branch,l.region_code,v.reason,vb.display_name,v.reviewed_at_utc FROM itam_data.ad_run_line l LEFT JOIN itam_data.delivery d ON d.delivery_id=l.delivery_id " +
            "LEFT JOIN itam_data.asset a ON a.asset_id=l.asset_id LEFT JOIN itam_access.employee e ON e.employee_id=d.employee_id " +
            "LEFT JOIN itam_data.ad_review v ON v.computer_name=l.computer_name AND v.result=l.result AND v.fingerprint=l.fingerprint LEFT JOIN itam_access.employee vb ON vb.employee_id=v.reviewed_by " +
            "WHERE l.run_id=@r ORDER BY l.computer_name", r => (Region: S(r, 17), Row: new
            {
                id = r.GetInt32(0), result = r.GetString(1), resultLabel = AdResultLabel(r.GetString(1)), computerName = r.GetString(2), ou = S(r, 3), enabled = r.IsDBNull(4) ? (bool?)null : r.GetBoolean(4),
                lastLogon = r.IsDBNull(5) ? (DateTime?)null : Utc(r, 5), created = r.IsDBNull(6) ? (DateTime?)null : Utc(r, 6), os = S(r, 7), reviewed = r.GetBoolean(8),
                deliveryId = I(r, 9), deliveryNo = S(r, 10), deliveredOn = Day(r, 11), ownerType = S(r, 12), assetNo = S(r, 13), employee = S(r, 14), username = S(r, 15), branch = S(r, 16),
                region = S(r, 17), reviewReason = S(r, 18), reviewedBy = S(r, 19), reviewedAt = r.IsDBNull(20) ? (DateTime?)null : Utc(r, 20)
            }), token, ("@r", run))).Where(x => s.ViewsAll || (x.Region is string g && s.CollectRegion(g))).Select(x => x.Row).ToList();
        return new
        {
            runs, run, lines, canRun = s.AdCompareRun, canReview = s.AdCompareReview, inactiveDays = AdInactiveDays,
            results = AdResults.Select(r => new { code = r, label = AdResultLabel(r), count = lines.Count(l => l.result == r && !l.reviewed) })
        };
    }

    public async Task ReviewAdLine(ItamUser user, ItamAdReview x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.AdCompareReview, "Only the System Administrator, the IT Assets Officer and the IT Manager mark lines reviewed.");
        var l = (await Rows(c, tx, "SELECT computer_name,result,fingerprint FROM itam_data.ad_run_line WHERE line_id=@i", r => (Name: r.GetString(0), Result: r.GetString(1), Print: r.GetString(2)), token, ("@i", x.LineId ?? 0))).SingleOrDefault();
        if (l.Name is null) throw new ArgumentException("The line was not found.");
        if (l.Result == "Matched") throw new ArgumentException("A matched computer needs no review.");
        var reason = Text(x.Reason, "Reason (for example a training or test computer)", 500);
        await Exec(c, tx, "DELETE itam_data.ad_review WHERE computer_name=@n AND result=@r; INSERT itam_data.ad_review(computer_name,result,fingerprint,reason,reviewed_by) VALUES(@n,@r,@f,@t,@b)", token,
            ("@n", l.Name), ("@r", l.Result), ("@f", l.Print), ("@t", reason), ("@b", s.Id));
        await Exec(c, tx, "UPDATE itam_data.ad_run_line SET reviewed=1 WHERE computer_name=@n AND result=@r AND fingerprint=@f", token, ("@n", l.Name), ("@r", l.Result), ("@f", l.Print));
        await Audit(c, tx, s.Id, "adcompare", l.Name, "Reviewed", null, new { l.Result, reason }, null, token);
        await tx.CommitAsync(token);
    }

    public async Task UnreviewAdLine(ItamUser user, ItamAdReview x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.AdCompareReview, "Only the System Administrator, the IT Assets Officer and the IT Manager change reviewed lines.");
        var l = (await Rows(c, tx, "SELECT computer_name,result FROM itam_data.ad_run_line WHERE line_id=@i", r => (Name: r.GetString(0), Result: r.GetString(1)), token, ("@i", x.LineId ?? 0))).SingleOrDefault();
        if (l.Name is null) throw new ArgumentException("The line was not found.");
        await Exec(c, tx, "DELETE itam_data.ad_review WHERE computer_name=@n AND result=@r", token, ("@n", l.Name), ("@r", l.Result));
        await Exec(c, tx, "UPDATE itam_data.ad_run_line SET reviewed=0 WHERE computer_name=@n AND result=@r", token, ("@n", l.Name), ("@r", l.Result));
        await Audit(c, tx, s.Id, "adcompare", l.Name, "Review removed", null, new { l.Result }, null, token);
        await tx.CommitAsync(token);
    }

    // Licenses ------------------------------------------------------------------------------------------------------------------------------
    /// <summary>Software licenses (asset type Software: name, seats, key kept encrypted) and Windows keys of computers, with who holds them.
    /// Keys are not in this list: each one opens with its own logged view.</summary>
    public async Task<object> Licenses(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.LicensesView, "You cannot see licenses.");
        var rows = await Rows(c, null, "SELECT a.asset_id,a.asset_no,a.type_code,a.status,g.region_code,g.name_en,CONCAT(mf.name_en,' ',mo.name_en)," +
            "(SELECT value FROM itam_data.asset_attribute x WHERE x.asset_id=a.asset_id AND x.attr_code=N'Software'),(SELECT value FROM itam_data.asset_attribute x WHERE x.asset_id=a.asset_id AND x.attr_code=N'Seats')," +
            "e.display_name,e.account,d.delivery_no,k.secret_code,a.serial_no,a.service_tag FROM itam_data.asset a JOIN itam_ref.location g ON g.location_id=a.location_id " +
            "LEFT JOIN itam_ref.manufacturer mf ON mf.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id " +
            "LEFT JOIN itam_data.delivery d ON d.asset_id=a.asset_id AND d.received_at_utc IS NULL LEFT JOIN itam_access.employee e ON e.employee_id=d.employee_id " +
            "LEFT JOIN itam_data.asset_secret k ON k.asset_id=a.asset_id AND k.secret_code=CASE a.type_code WHEN N'SOF' THEN N'SoftwareKey' ELSE N'OsKey' END " +
            "WHERE a.type_code=N'SOF' OR (a.type_code=N'PC' AND k.secret_code IS NOT NULL) ORDER BY a.asset_no",
            r => (Type: r.GetString(2), Region: r.GetString(4), Row: new
            {
                id = r.GetInt32(0), assetNo = r.GetString(1), status = ItamStatuses.Label(r.GetString(3)), statusCode = r.GetString(3), region = r.GetString(4), keptAt = r.GetString(5), model = S(r, 6)?.Trim(),
                software = S(r, 7), seats = int.TryParse(S(r, 8), out var n) ? n : (int?)null, holder = S(r, 9), username = S(r, 10), deliveryNo = S(r, 11), hasKey = !r.IsDBNull(12),
                serial = S(r, 13) ?? S(r, 14), canKey = false
            }), token);
        var visible = rows.Where(x => s.CanView(x.Type, x.Region)).Select(x => (x.Type, Row: x.Row with { canKey = x.Row.hasKey && s.CanSeeKeys(x.Region) })).ToList();
        var software = visible.Where(x => x.Type == "SOF").Select(x => x.Row).ToList();
        return new
        {
            software, windows = visible.Where(x => x.Type == "PC").Select(x => x.Row),
            summary = software.GroupBy(x => x.software ?? "(no name)").OrderBy(g => g.Key).Select(g => new
            {
                software = g.Key, licenses = g.Count(), seats = g.Sum(x => x.seats ?? 1), inUse = g.Count(x => x.statusCode == "InUse"), inStore = g.Count(x => x.statusCode == "InStore"),
                withoutKey = g.Count(x => !x.hasKey)
            })
        };
    }
}
