using System.Data.Common;

namespace Itam.App;

/// <summary>Alerts (owner, 22 October 2026): one page for every role with everything waiting for the signed-in person, across
/// all modules: decisions (as manager or approver), handover, tasks, movements, requests, ink, maintenance and end of life.
/// Each alert has a count, the first references, and the page where the work is done. The total shows on the menu.</summary>
public sealed partial class ItamStore
{
    public sealed record ItamAlert(string Group, string Key, string Label, string Hint, int Count, string[] Refs, string Href, bool Urgent);

    internal static async Task<List<ItamAlert>> AlertItems(DbConnection c, ItamScope s, CancellationToken token)
    {
        var list = new List<ItamAlert>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        void Count(string group, string key, string label, string hint, int n, string href, bool urgent = false)
        { if (n > 0) list.Add(new(group, key, label, hint, n, [], href, urgent)); }
        void Refs(string group, string key, string label, string hint, IEnumerable<string> refs, string href, bool urgent = false)
        { var r = refs.Distinct().ToList(); if (r.Count > 0) list.Add(new(group, key, label, hint, r.Count, r.Take(6).ToArray(), href, urgent)); }
        async Task<List<(string No, string? Region)>> Q(string sql, params (string, object?)[] p) => await Rows(c, null, sql, r => (r.GetString(0), S(r, 1)), token, p);
        bool In(string? region) => region is not null && s.CanHandover(region);
        // Employees follow their own gate passes on My IT Assets; IT and approvers on Movements.
        var mv = s.CanHandoverAny || s.ViewsAll || s.MovementApproverRole ? "/movements.html" : "/my.html";

        // Decisions waiting for this person.
        Count("Decisions", "requests", "Device and accessory requests to decide", "As the direct manager, or as the IT Manager.", await RequestApprovalsCount(c, s, token), "/my.html", true);
        Count("Decisions", "ink", "Ink requests to approve", "Sent to you for approval.", await PendingApprovals(c, s, token), "/ink.html", true);
        Count("Decisions", "maintenance", "Maintenance orders to approve", "With the supplier's quotation.", await MaintenanceApprovalsCount(c, s, token), "/maintenance.html", true);
        Count("Decisions", "movements", "Gate passes to approve", "Branch transfers and exit passes.", await MovementApprovalsCount(c, s, token), "/movements.html", true);
        Count("Decisions", "disposals", "Disposals to approve", "Approval alone does not dispose.", await DisposalApprovalsCount(c, s, token), "/disposals.html", true);
        Count("Decisions", "loss", "Loss cases waiting for you", "Investigator to assign, findings to record, decision or write-off.", await LossActionsCount(c, s, token), "/loss.html", true);

        // Movements: dispatch, receive, discrepancies, overdue returns (also for employees: their own passes).
        Refs("Movements", "dispatch", "Gate passes to dispatch", "Approved: take the assets out within the pass validity.",
            (await Q("SELECT m.movement_no,g.region_code FROM itam_data.movement m JOIN itam_ref.location g ON g.location_id=m.from_location_id WHERE m.kind<>N'Warehouse' AND m.status=N'Approved' " +
                "AND @t BETWEEN m.exit_date AND m.valid_until", ("@t", DateParam(today))))
            .Where(x => In(x.Region)).Select(x => x.No)
            .Concat(await Rows(c, null, "SELECT movement_no FROM itam_data.movement WHERE kind<>N'Warehouse' AND status=N'Approved' AND @t BETWEEN exit_date AND valid_until AND (sender_id=@me OR (sender_id IS NULL AND (requested_by=@me OR employee_id=@me)))",
                r => r.GetString(0), token, ("@t", DateParam(today)), ("@me", s.Id))), mv);
        Refs("Movements", "warehouse", "Warehouse transfers to dispatch", "Stock reserved at your warehouse.",
            (await Q("SELECT m.movement_no,g.region_code FROM itam_data.movement m JOIN itam_ref.location g ON g.location_id=m.from_location_id WHERE m.kind=N'Warehouse' AND m.status IN(N'Submitted',N'PartiallyDispatched')"))
            .Where(x => In(x.Region)).Select(x => x.No), "/movements.html");
        Refs("Movements", "receive", "Assets to receive", "You are the named receiver: confirm each asset.",
            await Rows(c, null, "SELECT DISTINCT m.movement_no FROM itam_data.movement m JOIN itam_data.movement_line l ON l.movement_id=m.movement_id WHERE m.receiver_id=@me AND m.kind<>N'Exit' AND l.state=N'Dispatched'",
                r => r.GetString(0), token, ("@me", s.Id)), mv, true);
        if (s.MovementApproverRole)
            Refs("Movements", "discrepancies", "Transfer discrepancies to resolve", "Missing, damaged or incorrect assets.",
                await Rows(c, null, "SELECT movement_no FROM itam_data.movement WHERE status=N'ReceiptDisputed'", r => r.GetString(0), token), "/movements.html", true);
        Refs("Movements", "returns", "Exit passes overdue for return", "Returnable assets not back by the expected date.",
            (await Q("SELECT m.movement_no,g.region_code FROM itam_data.movement m JOIN itam_ref.location g ON g.location_id=m.from_location_id WHERE m.kind=N'Exit' AND m.status=N'Out'  AND m.expected_date<@t",
                ("@t", DateParam(today)))).Where(x => In(x.Region)).Select(x => x.No)
            .Concat(await Rows(c, null, "SELECT movement_no FROM itam_data.movement WHERE kind=N'Exit' AND status=N'Out' AND expected_date<@t AND (employee_id=@me OR requested_by=@me OR sender_id=@me)",
                r => r.GetString(0), token, ("@t", DateParam(today)), ("@me", s.Id))), mv, true);

        // Handover and infrastructure tasks.
        if (s.CanHandoverAny)
            Refs("Handover", "signed", "Delivery notes waiting for the signed copy", "Print, have the employee sign on paper, upload the scan.",
                (await Q("SELECT d.delivery_no,g.region_code FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id JOIN itam_ref.location g ON g.location_id=a.location_id " +
                    "WHERE d.received_at_utc IS NULL AND d.signed_file_id IS NULL")).Where(x => In(x.Region)).Select(x => x.No), "/handover.html");
        Count("Handover", "tasks", "AD, Endpoint Central and CUCM removals", "After a return or a sale.", await OpenTaskCount(c, s, token), "/tasks.html");

        // Requests (IT side).
        if (s.Officer || s.Specialist || s.ViewsAll)
        {
            var reqs = await Rows(c, null, "SELECT request_no,region_code,status,assigned_to FROM itam_data.request WHERE status IN(N'InReview',N'PricingInRegion',N'Approved',N'StockAssigned',N'Ordered',N'CashPurchase')",
                r => (No: r.GetString(0), Region: S(r, 1), Status: r.GetString(2), Assigned: G0(r, 3)), token);
            bool Mine((string No, string? Region, string Status, Guid? Assigned) r) => s.Officer || r.Assigned == s.Id;
            Refs("Requests", "review", "Requests to review", "Choose the source: stock, new device or IT accessory.", reqs.Where(r => r.Status == "InReview" && s.Officer).Select(r => r.No), "/requests.html");
            Refs("Requests", "pricing", "Accessories to price", "Local prices for the manager's cost approval.", reqs.Where(r => r.Status == "PricingInRegion" && Mine(r)).Select(r => r.No), "/requests.html");
            Refs("Requests", "order", "Approved requests to order", "Record the PO or the order.", reqs.Where(r => r.Status == "Approved" && s.Officer).Select(r => r.No), "/requests.html");
            Refs("Requests", "buy", "Accessories to buy", "Petty cash purchase with the receipt.", reqs.Where(r => r.Status == "CashPurchase" && Mine(r)).Select(r => r.No), "/requests.html");
            Refs("Requests", "deliver", "Requests to deliver", "Deliver the device and link the delivery note.", reqs.Where(r => r.Status is "StockAssigned" or "Ordered" && Mine(r)).Select(r => r.No), "/requests.html");
        }

        // Ink (handlers of the region).
        if (s.InkHandle)
        {
            var ink = await Rows(c, null, "SELECT request_no,region_code,status,CASE WHEN printer_asset_id IS NULL THEN 0 ELSE 1 END FROM itam_data.ink_request WHERE status IN(N'Submitted',N'Approved',N'Ordered')",
                r => (No: r.GetString(0), Region: S(r, 1), Status: r.GetString(2), Registered: r.GetInt32(3) == 1), token);
            Refs("Ink", "printer", "Ink requests for a printer not in IT Assets", "Link or register the printer.", ink.Where(i => i.Status == "Submitted" && !i.Registered && s.CanInkPrinter(i.Region)).Select(i => i.No), "/ink.html");
            Refs("Ink", "send", "Ink requests to send for approval", "Choose the approving manager.", ink.Where(i => i.Status == "Submitted" && i.Registered && s.CanInkSend(i.Region)).Select(i => i.No), "/ink.html");
            Refs("Ink", "deliver", "Approved ink to order or deliver", "From the shelf, or on a supplier order.", ink.Where(i => i.Status is "Approved" or "Ordered" && s.CanInk(i.Region)).Select(i => i.No), "/ink.html");
        }

        // Maintenance (IT).
        if (s.MaintenanceManage)
        {
            Refs("Maintenance", "send", "Approved maintenance orders to send", "Record the actual dispatch to the supplier.",
                await Rows(c, null, "SELECT order_no FROM itam_data.maintenance_order WHERE status=N'Approved'", r => r.GetString(0), token), "/maintenance.html");
            Refs("Maintenance", "overdue", "Assets overdue at the supplier", "Past the expected return date.",
                await Rows(c, null, "SELECT order_no FROM itam_data.maintenance_order WHERE status=N'WithSupplier' AND expected_return<@t", r => r.GetString(0), token, ("@t", DateParam(today))), "/maintenance.html", true);
        }

        // End of life.
        Count("End of life", "collect", "Assets to collect from leavers", "Receive them back, or record a sale.", await CollectCount(c, s, token), "/leavers.html", true);
        if (s.ClearanceComplete)
        {
            var ready = new List<string>();
            foreach (var (no, emp) in await Rows(c, null, "SELECT clearance_no,employee_id FROM itam_data.clearance WHERE status=N'InProgress' AND employee_id<>@me", r => (r.GetString(0), r.GetGuid(1)), token, ("@me", s.Id)))
                if (await ClearanceBlockers(c, null, emp, token) is (0, 0)) ready.Add(no);
            Refs("End of life", "clearance", "Clearances ready to complete", "Nothing is left with the employee.", ready, "/leavers.html");
        }
        if (s.UserDisableTask)
            Refs("End of life", "disable", "Users to disable in AD", "The clearance is completed: disable the user and keep it.",
                await Rows(c, null, "SELECT clearance_no FROM itam_data.clearance WHERE disable_status=N'Waiting'", r => r.GetString(0), token), "/leavers.html", true);
        if (s.DisposalComplete)
            Refs("End of life", "dispose", "Approved disposals to complete", "Record the completion with its evidence.",
                await Rows(c, null, "SELECT disposal_no FROM itam_data.disposal WHERE status=N'Approved'", r => r.GetString(0), token), "/disposals.html");
        if (s.WriteOffRequest)
        {
            var loss = await Rows(c, null, "SELECT case_no,status,custodian_id FROM itam_data.loss_case WHERE status IN(N'AwaitingAction',N'RecoveredReview')", r => (No: r.GetString(0), Status: r.GetString(1), Cu: G0(r, 2)), token);
            Refs("End of life", "settle", "Loss settlements to record", "The decision requires a settlement.", loss.Where(l => l.Status == "AwaitingAction" && l.Cu != s.Id).Select(l => l.No), "/loss.html");
            Refs("End of life", "recovered", "Recovered assets to review", "Before they go back to stock.", loss.Where(l => l.Status == "RecoveredReview").Select(l => l.No), "/loss.html");
        }
        if (s.AdCompareReview)
            Count("End of life", "adcompare", "AD comparison differences", "In the latest run, not reviewed yet.",
                Convert.ToInt32(await Scalar(c, null, "SELECT COUNT(*) FROM itam_data.ad_run_line WHERE run_id=(SELECT MAX(run_id) FROM itam_data.ad_run) AND result<>N'Matched' AND reviewed=0", token)), "/adcompare.html");
        return list;
    }

    public async Task<object> Alerts(ItamUser user, CancellationToken token)
    {
        await Expire(token);
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var items = await AlertItems(c, s, token);
        var groups = new[] { "Decisions", "Movements", "Handover", "Requests", "Ink", "Maintenance", "End of life" };
        return new
        {
            total = items.Sum(i => i.Count),
            groups = groups.Select(g => new { name = g, items = items.Where(i => i.Group == g).Select(i => new { key = i.Key, label = i.Label, hint = i.Hint, count = i.Count, refs = i.Refs, href = i.Href, urgent = i.Urgent }) })
                .Where(g => g.items.Any())
        };
    }
}
