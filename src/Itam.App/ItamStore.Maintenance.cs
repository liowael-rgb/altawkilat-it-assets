using System.Data;
using System.Data.Common;

namespace Itam.App;

public sealed record ItamMaintenanceInput(string? Kind, DateOnly? OpenedOn, string? Problem, string? DoneBy, int? SupplierId, bool TakeIn);
public sealed record ItamMaintenanceClose(DateOnly? ClosedOn, string? WorkDone, string? Parts, decimal? Cost, int? InvoiceId);

/// <summary>Maintenance MT-n (owner, 22 October 2026): repairs, preventive maintenance, upgrades and parts replaced on an asset,
/// by IT or by a supplier. A device taken in for repair is In Repair until the maintenance is closed, then back to its
/// previous status. IT of the asset's region (and the IT Administrator) record maintenance.</summary>
public sealed partial class ItamStore
{
    public static readonly (string Code, string Label)[] MaintenanceKinds = [("Repair", "Repair"), ("Preventive", "Preventive maintenance"), ("Upgrade", "Upgrade"), ("Parts", "Parts replaced")];
    static string MaintenanceKindLabel(string k) => MaintenanceKinds.FirstOrDefault(x => x.Code == k).Label ?? k;
    static bool CanMaintain(ItamScope s, string region) => s.CanHandover(region) || s.ItAdmin;

    public async Task<object> OpenMaintenance(ItamUser user, int assetId, ItamMaintenanceInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var a = (await Rows(c, tx, "SELECT a.asset_no,a.status,g.region_code FROM itam_data.asset a WITH (UPDLOCK) JOIN itam_ref.location g ON g.location_id=a.location_id WHERE a.asset_id=@a",
            r => (No: r.GetString(0), Status: r.GetString(1), Region: r.GetString(2)), token, ("@a", assetId))).SingleOrDefault();
        if (a.No is null) throw new ArgumentException("The asset was not found.");
        Require(CanMaintain(s, a.Region), "You record maintenance for assets of your regions only.");
        if (a.Status is "Disposed" or "Sold" or "Lost" or "Expired") throw new ArgumentException($"{a.No} is {ItamStatuses.Label(a.Status)}.");
        if (await Scalar(c, tx, "SELECT maintenance_no FROM itam_data.maintenance WHERE asset_id=@a AND status=N'Open'", token, ("@a", assetId)) is string open)
            throw new ArgumentException($"{a.No} already has open maintenance {open}: close it first.");
        var kind = MaintenanceKinds.Any(k => k.Code == x.Kind) ? x.Kind! : throw new ArgumentException("Choose the kind of maintenance.");
        var by = x.DoneBy is "IT" or "Supplier" ? x.DoneBy : throw new ArgumentException("Choose who does the work: IT or a supplier.");
        if (by == "Supplier" && await Scalar(c, tx, "SELECT 1 FROM itam_ref.supplier WHERE supplier_id=@s AND active=1", token, ("@s", x.SupplierId ?? 0)) is null) throw new ArgumentException("Choose the supplier.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3)); var on = x.OpenedOn ?? today;
        if (on > today) throw new ArgumentException("The date cannot be in the future.");
        var problem = Text(x.Problem, "Problem or work needed", 1000);
        var takeIn = x.TakeIn && a.Status != "InRepair";
        var no = $"MT-{await Next(c, tx, "Maintenance", token)}";
        var id = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_data.maintenance(maintenance_no,asset_id,kind,opened_on,problem,done_by,supplier_id,status,previous_status,created_by) OUTPUT inserted.maintenance_id " +
            "VALUES(@n,@a,@k,@o,@p,@b,@s,N'Open',@ps,@u)", token, ("@n", no), ("@a", assetId), ("@k", kind), ("@o", DateParam(on)), ("@p", problem), ("@b", by), ("@s", by == "Supplier" ? x.SupplierId : null),
            ("@ps", takeIn ? a.Status : null), ("@u", s.Id)));
        if (takeIn) await Exec(c, tx, "UPDATE itam_data.asset SET status=N'InRepair' WHERE asset_id=@a", token, ("@a", assetId));
        await Event(c, tx, assetId, s.Id, "Maintenance", $"{no} opened: {MaintenanceKindLabel(kind)}, {problem}{(takeIn ? " (taken in: In Repair)" : "")}", token);
        await Audit(c, tx, s.Id, "maintenance", no, "Open", null, new { asset = a.No, kind, by, x.SupplierId, takeIn }, null, token);
        await tx.CommitAsync(token);
        return new { id, maintenanceNo = no };
    }

    public async Task CloseMaintenance(ItamUser user, int maintenanceId, ItamMaintenanceClose x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var m = (await Rows(c, tx, "SELECT m.maintenance_no,m.asset_id,m.status,m.previous_status,m.opened_on,g.region_code FROM itam_data.maintenance m WITH (UPDLOCK) JOIN itam_data.asset a ON a.asset_id=m.asset_id " +
            "JOIN itam_ref.location g ON g.location_id=a.location_id WHERE m.maintenance_id=@m",
            r => (No: r.GetString(0), Asset: r.GetInt32(1), Status: r.GetString(2), Previous: S(r, 3), Opened: Day(r, 4)!.Value, Region: r.GetString(5)), token, ("@m", maintenanceId))).SingleOrDefault();
        if (m.No is null) throw new ArgumentException("The maintenance was not found.");
        Require(CanMaintain(s, m.Region), "You record maintenance for assets of your regions only.");
        if (m.Status != "Open") throw new ArgumentException($"{m.No} is already closed.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3)); var on = x.ClosedOn ?? today;
        if (on > today || on < m.Opened) throw new ArgumentException("The closing date is between the opening date and today.");
        var work = Text(x.WorkDone, "Work done", 1000);
        if (x.Cost is < 0) throw new ArgumentException("The cost cannot be negative.");
        if (x.InvoiceId is int inv && await Scalar(c, tx, "SELECT 1 FROM itam_data.invoice WHERE invoice_id=@i AND category=N'PartsMaintenance'", token, ("@i", inv)) is null)
            throw new ArgumentException("Choose a parts and maintenance invoice.");
        await Exec(c, tx, "UPDATE itam_data.maintenance SET status=N'Closed',closed_on=@d,work_done=@w,parts=@p,cost=@c,invoice_id=@i,closed_by=@b WHERE maintenance_id=@m", token,
            ("@d", DateParam(on)), ("@w", work), ("@p", Optional(x.Parts, "Parts", 500)), ("@c", x.Cost), ("@i", x.InvoiceId), ("@b", s.Id), ("@m", maintenanceId));
        if (m.Previous is not null) await Exec(c, tx, "UPDATE itam_data.asset SET status=@st WHERE asset_id=@a AND status=N'InRepair'", token, ("@st", m.Previous), ("@a", m.Asset));
        await Event(c, tx, m.Asset, s.Id, "Maintenance", $"{m.No} closed: {work}{(m.Previous is null ? "" : $" (back to {ItamStatuses.Label(m.Previous)})")}", token);
        await Audit(c, tx, s.Id, "maintenance", m.No, "Close", null, new { work, x.Cost, x.InvoiceId }, null, token);
        await tx.CommitAsync(token);
    }

    /// <summary>The asset's history in three parts: deliveries, receipts (returns) and maintenance.</summary>
    async Task<object> AssetHistory(DbConnection c, ItamScope s, int assetId, string region, CancellationToken token)
    {
        var deliveries = await Rows(c, null, "SELECT d.delivery_id,d.delivery_no,e.display_name,e.account,e.sap_id,d.delivered_on,d.owner_type,ib.display_name,d.signed_at_utc,d.received_at_utc,rb.display_name,d.receipt_condition,rl.name_en,ta.assessment_no,ta.recommendation " +
            "FROM itam_data.delivery d JOIN itam_access.employee e ON e.employee_id=d.employee_id LEFT JOIN itam_access.employee ib ON ib.employee_id=d.issued_by LEFT JOIN itam_access.employee rb ON rb.employee_id=d.received_by " +
            "LEFT JOIN itam_ref.location rl ON rl.location_id=d.receipt_location_id LEFT JOIN itam_data.assessment ta ON ta.delivery_id=d.delivery_id WHERE d.asset_id=@a ORDER BY d.delivery_id DESC",
            r => new { id = r.GetInt32(0), no = r.GetString(1), employee = r.GetString(2), username = r.GetString(3), sapId = S(r, 4), deliveredOn = Day(r, 5), ownerType = S(r, 6), issuedBy = S(r, 7),
                signedAt = r.IsDBNull(8) ? (DateTime?)null : Utc(r, 8), receivedAt = r.IsDBNull(9) ? (DateTime?)null : Utc(r, 9), receivedBy = S(r, 10), condition = S(r, 11), receivedInto = S(r, 12),
                receiptNo = r.IsDBNull(9) ? null : r.GetString(1) + "-R", assessmentNo = S(r, 13), recommendation = r.IsDBNull(14) ? null : RecommendationLabel(r.GetString(14)) }, token, ("@a", assetId));
        var maintenance = await Rows(c, null, "SELECT m.maintenance_id,m.maintenance_no,m.kind,m.opened_on,m.problem,m.done_by,su.name_en,m.status,m.closed_on,m.work_done,m.parts,m.cost,i.invoice_no,cb.display_name,m.previous_status " +
            "FROM itam_data.maintenance m LEFT JOIN itam_ref.supplier su ON su.supplier_id=m.supplier_id LEFT JOIN itam_data.invoice i ON i.invoice_id=m.invoice_id LEFT JOIN itam_access.employee cb ON cb.employee_id=m.created_by " +
            "WHERE m.asset_id=@a ORDER BY m.maintenance_id DESC",
            r => new { id = r.GetInt32(0), no = r.GetString(1), kind = MaintenanceKindLabel(r.GetString(2)), openedOn = Day(r, 3), problem = r.GetString(4), doneBy = r.GetString(5) == "IT" ? "IT" : S(r, 6) ?? "Supplier",
                status = r.GetString(7), closedOn = Day(r, 8), workDone = S(r, 9), parts = S(r, 10), cost = r.IsDBNull(11) ? (decimal?)null : r.GetDecimal(11), invoice = S(r, 12), by = S(r, 13), takenIn = !r.IsDBNull(14) }, token, ("@a", assetId));
        var invoices = CanMaintain(s, region) ? await Rows(c, null, "SELECT TOP 50 i.invoice_id,i.invoice_no,su.name_en FROM itam_data.invoice i JOIN itam_ref.supplier su ON su.supplier_id=i.supplier_id WHERE i.category=N'PartsMaintenance' ORDER BY i.invoice_date DESC",
            r => new { id = r.GetInt32(0), no = r.GetString(1), supplier = r.GetString(2) }, token) : [];
        return new { deliveries, maintenance, canMaintain = CanMaintain(s, region), kinds = MaintenanceKinds.Select(k => new { code = k.Code, label = k.Label }), maintenanceInvoices = invoices };
    }
}
