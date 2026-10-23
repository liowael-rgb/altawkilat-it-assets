using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace Itam.App;

public sealed record ItamRemoveInput(string? Reason);

/// <summary>Removing an asset registered by mistake (owner, 23 October 2026): the IT Manager only, with a reason, and only while the
/// asset was never used. The audit log keeps a full copy of the removed record. A used asset is corrected with Edit, or leaves
/// through loss, disposal or sale.</summary>
public sealed partial class ItamStore
{
    /// <summary>What the asset was used in; empty = it can be removed.</summary>
    static async Task<List<string>> AssetUses(DbConnection c, DbTransaction? tx, int id, CancellationToken token)
    {
        (string Sql, string What)[] checks =
        [
            ("SELECT COUNT(*) FROM itam_data.delivery WHERE asset_id=@a", "delivery notes"),
            ("SELECT COUNT(*) FROM itam_data.task WHERE asset_id=@a", "IT tasks"),
            ("SELECT COUNT(*) FROM itam_data.movement_line WHERE asset_id=@a", "transfers or gate passes"),
            ("SELECT COUNT(*) FROM itam_data.maintenance_order WHERE asset_id=@a", "maintenance orders"),
            ("SELECT COUNT(*) FROM itam_data.ink_request WHERE printer_asset_id=@a", "ink requests"),
            ("SELECT COUNT(*) FROM itam_data.request WHERE reserved_asset_id=@a OR replaced_asset_id=@a", "device requests"),
            ("SELECT COUNT(*) FROM itam_data.loss_case WHERE asset_id=@a", "loss cases"),
            ("SELECT COUNT(*) FROM itam_data.disposal WHERE asset_id=@a", "disposals"),
            ("SELECT COUNT(*) FROM itam_data.sale WHERE asset_id=@a", "sales"),
        ];
        var used = new List<string>();
        foreach (var (sql, what) in checks)
            if (Convert.ToInt32(await Scalar(c, tx, sql, token, ("@a", id))) is int n && n > 0) used.Add($"{n} {what}");
        return used;
    }

    public async Task<object> RemoveAsset(ItamUser user, int id, ItamRemoveInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.Manager, "Only the IT Manager removes an asset registered by mistake.");
        var reason = Text(x.Reason, "Reason", 500);
        var no = await Scalar(c, tx, "SELECT asset_no FROM itam_data.asset WITH (UPDLOCK,HOLDLOCK) WHERE asset_id=@a", token, ("@a", id)) as string ?? throw new ArgumentException("The asset was not found.");
        var used = await AssetUses(c, tx, id, token);
        if (used.Count > 0) throw new ArgumentException($"{no} cannot be removed: it has {string.Join(", ", used)}. Correct it with Edit, or close it with a loss, disposal or sale.");
        // The full record for the audit log: the asset row, its fields, which keys it had (never the keys) and its history.
        var row = await Scalar(c, tx, "SELECT * FROM itam_data.asset WHERE asset_id=@a FOR JSON PATH, WITHOUT_ARRAY_WRAPPER", token, ("@a", id)) as string;
        var fields = await Rows(c, tx, "SELECT attr_code,value FROM itam_data.asset_attribute WHERE asset_id=@a", r => new { code = r.GetString(0), value = r.GetString(1) }, token, ("@a", id));
        var keys = await Rows(c, tx, "SELECT secret_code FROM itam_data.asset_secret WHERE asset_id=@a", r => r.GetString(0), token, ("@a", id));
        var history = await Rows(c, tx, "SELECT at_utc,kind,detail FROM itam_data.asset_event WHERE asset_id=@a ORDER BY event_id", r => new { at = Utc(r, 0), kind = r.GetString(1), detail = S(r, 2) }, token, ("@a", id));
        await Exec(c, tx, "UPDATE itam_data.ad_run_line SET asset_id=NULL WHERE asset_id=@a", token, ("@a", id));
        await Exec(c, tx, "DELETE itam_data.asset_attribute WHERE asset_id=@a", token, ("@a", id));
        await Exec(c, tx, "DELETE itam_data.asset_secret WHERE asset_id=@a", token, ("@a", id));
        await Exec(c, tx, "DELETE itam_data.asset_event WHERE asset_id=@a", token, ("@a", id));
        await Exec(c, tx, "DELETE itam_data.asset WHERE asset_id=@a", token, ("@a", id));
        await Audit(c, tx, s.Id, "asset", no, "Remove", new { asset = row is null ? (JsonElement?)null : JsonDocument.Parse(row).RootElement.Clone(), fields, keys, history }, null, reason, token);
        await tx.CommitAsync(token);
        return new { assetNo = no };
    }
}
