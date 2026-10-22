using System.Data.Common;

namespace Itam.App;

public sealed record ItamQuickDecision(string? Kind, int Id, string? Stage, bool Approve, string? Reason);

/// <summary>One-click approval from the email (owner, 22 October 2026; rules of the email-template README). The Approve and Reject
/// buttons open the decision page of IT Assets with the record and the approval step it was sent for. Opening a link never
/// decides anything (a mail scanner or preview only reads); the page then sends the decision as a signed-in, server-checked
/// request: the person must be the eligible approver now (or their backup on leave), and the record must still be at that
/// step, so an old email, a second click or another step cannot act. Approve needs no other click; Reject asks for a reason.
/// The decision goes through the same rules as on the record's page.</summary>
public sealed partial class ItamStore
{
    sealed record QuickRow(string No, string Status, string StatusLabel, string Title, string Summary, Guid Requester, Guid? Manager, string? DecidedBy, DateTime? DecidedAt, string Link);

    async Task<QuickRow> LoadQuick(DbConnection c, string kind, int id, CancellationToken token)
    {
        QuickRow? row = kind switch
        {
            "request" => (await Rows(c, null, "SELECT q.request_no,q.status,e.display_name,q.description,q.requested_by,q.manager_id,COALESCE(cb.display_name,ab.display_name,rb.display_name),COALESCE(q.cost_approved_at_utc,q.approved_at_utc),q.request_type " +
                "FROM itam_data.request q JOIN itam_access.employee e ON e.employee_id=q.employee_id LEFT JOIN itam_access.employee ab ON ab.employee_id=q.approved_by LEFT JOIN itam_access.employee cb ON cb.employee_id=q.cost_approved_by " +
                "LEFT JOIN itam_access.employee rb ON rb.employee_id=q.rejected_by WHERE q.request_id=@i",
                r => new QuickRow(r.GetString(0), r.GetString(1), RequestStatusLabel(r.GetString(1)), $"Device request {r.GetString(0)}", $"{r.GetString(2)} · {(r.GetString(8) == "Replacement" ? "Replacement" : "New device")} · {r.GetString(3)}",
                    r.GetGuid(4), G0(r, 5), S(r, 6), r.IsDBNull(7) ? null : Utc(r, 7), "/request.html?id=" + id), token, ("@i", id))).SingleOrDefault(),
            "ink" => (await Rows(c, null, "SELECT q.request_no,q.status,e.display_name,q.toner,q.quantity,q.employee_id,q.manager_id,db.display_name,q.decided_at_utc,COALESCE(a.asset_no,N'S/N '+q.new_serial) " +
                "FROM itam_data.ink_request q JOIN itam_access.employee e ON e.employee_id=q.employee_id LEFT JOIN itam_access.employee db ON db.employee_id=q.decided_by LEFT JOIN itam_data.asset a ON a.asset_id=q.printer_asset_id WHERE q.request_id=@i",
                r => new QuickRow(r.GetString(0), r.GetString(1), r.GetString(1) switch { "WaitingApproval" => "Waiting approval", var x => x }, $"Ink request {r.GetString(0)}", $"{r.GetString(2)} · {r.GetInt32(4)} × {r.GetString(3)} · printer {r.GetString(9)}",
                    r.GetGuid(5), G0(r, 6), S(r, 7), r.IsDBNull(8) ? null : Utc(r, 8), "/ink.html"), token, ("@i", id))).SingleOrDefault(),
            "maintenance" => (await Rows(c, null, "SELECT m.order_no,m.status,a.asset_no,su.name_en,m.cost,m.details,m.created_by,db.display_name,m.decided_at_utc FROM itam_data.maintenance_order m JOIN itam_data.asset a ON a.asset_id=m.asset_id " +
                "JOIN itam_ref.supplier su ON su.supplier_id=m.supplier_id LEFT JOIN itam_access.employee db ON db.employee_id=m.decided_by WHERE m.order_id=@i",
                r => new QuickRow(r.GetString(0), r.GetString(1), OrderStatusLabel(r.GetString(1)), $"Maintenance order {r.GetString(0)}", $"{r.GetString(2)} · {r.GetString(3)} · {Money(r.GetDecimal(4))} · {r.GetString(5)}",
                    r.GetGuid(6), null, S(r, 7), r.IsDBNull(8) ? null : Utc(r, 8), "/maintenance.html"), token, ("@i", id))).SingleOrDefault(),
            "movement" => (await Rows(c, null, "SELECT m.movement_no,m.status,m.purpose,fg.name_en,COALESCE(tg.name_en,m.destination),m.requested_by,db.display_name,m.decided_at_utc,m.kind,(SELECT COUNT(*) FROM itam_data.movement_line l WHERE l.movement_id=m.movement_id AND l.state<>N'Cancelled') " +
                "FROM itam_data.movement m JOIN itam_ref.location fg ON fg.location_id=m.from_location_id LEFT JOIN itam_ref.location tg ON tg.location_id=m.to_location_id LEFT JOIN itam_access.employee db ON db.employee_id=m.decided_by WHERE m.movement_id=@i",
                r => new QuickRow(r.GetString(0), r.GetString(8) == "Warehouse" ? "Warehouse" : r.GetString(1), MovementStatusLabel(r.GetString(1)), $"Gate pass {r.GetString(0)}", $"{r.GetString(2)} · {r.GetString(3)} → {S(r, 4)} · {r.GetInt32(9)} asset(s)",
                    r.GetGuid(5), null, S(r, 6), r.IsDBNull(7) ? null : Utc(r, 7), "/movement.html?id=" + id), token, ("@i", id))).SingleOrDefault(),
            "disposal" => (await Rows(c, null, "SELECT x.disposal_no,x.status,a.asset_no,t.name_en,x.reason,x.requested_by,db.display_name,x.decided_at_utc FROM itam_data.disposal x JOIN itam_data.asset a ON a.asset_id=x.asset_id " +
                "JOIN itam_ref.asset_type t ON t.type_code=a.type_code LEFT JOIN itam_access.employee db ON db.employee_id=x.decided_by WHERE x.disposal_id=@i",
                r => new QuickRow(r.GetString(0), r.GetString(1), DisposalStatusLabel(r.GetString(1)), $"Disposal {r.GetString(0)}", $"{r.GetString(2)} · {r.GetString(3)} · {r.GetString(4)}",
                    r.GetGuid(5), null, S(r, 6), r.IsDBNull(7) ? null : Utc(r, 7), "/disposals.html"), token, ("@i", id))).SingleOrDefault(),
            _ => throw new ArgumentException("Unknown decision.")
        };
        return row ?? throw new ArgumentException("The record was not found.");
    }

    /// <summary>The approval step each email is sent for: the record must still be there.</summary>
    static bool StageOk(string kind, string stage) => (kind, stage) switch
    {
        ("request", "WaitingRequestApproval" or "WaitingCostApproval") or ("ink", "WaitingApproval") or ("maintenance", "WaitingApproval") or ("movement", "Submitted") or ("disposal", "WaitingApproval") => true,
        _ => false
    };

    async Task<bool> EligibleNow(DbConnection c, ItamScope s, string kind, int id, QuickRow q, CancellationToken token) => kind switch
    {
        "request" => await ManagerOf(c, null, s, await LoadRequest(c, null, id, token), token),
        "ink" => q.Manager is Guid m && s.Id != q.Requester && await ActsFor(c, null, s.Id, m, token),
        "maintenance" => await MaintenanceApprover(c, null, s, q.Requester, token),
        "movement" => await MovementApprover(c, null, s, q.Requester, token),
        "disposal" => await ManagerDecides(c, null, s, token, q.Requester),
        _ => false
    };

    /// <summary>What the decision page shows (read only: opening the email link never decides).</summary>
    public async Task<object> QuickDecisionInfo(ItamUser user, string? kind, int id, string? stage, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var k = kind ?? ""; var q = await LoadQuick(c, k, id, token);
        var waiting = stage is not null && StageOk(k, stage) && q.Status == stage;
        var eligible = waiting && await EligibleNow(c, s, k, id, q, token);
        return new { kind = k, id, stage, no = q.No, title = q.Title, summary = q.Summary, status = q.StatusLabel, waiting, eligible, decidedBy = q.DecidedBy, decidedAt = q.DecidedAt, link = q.Link, me = s.Name };
    }

    /// <summary>The decision from the email page: same rules as on the record's page, only at the step the email was for.</summary>
    public async Task<object> QuickDecide(ItamUser user, ItamQuickDecision x, CancellationToken token)
    {
        var k = x.Kind ?? ""; var stage = x.Stage ?? "";
        if (!StageOk(k, stage)) throw new ArgumentException("This link is not a decision link.");
        await using (var c = await Open(token))
        {
            var q = await LoadQuick(c, k, x.Id, token);
            if (q.Status != stage) throw new ArgumentException($"{q.No} is no longer waiting for this decision: it is {q.StatusLabel}{(q.DecidedBy is null ? "" : $" ({q.DecidedBy}, {ItamMailLayout.When(q.DecidedAt)})")}.");
        }
        var decision = new ItamDecision(x.Approve, x.Approve ? null : x.Reason);
        switch (k)
        {
            case "request" when stage == "WaitingRequestApproval": await DecideRequest(user, x.Id, decision, token); break;
            case "request": await DecideCost(user, x.Id, decision, token); break;
            case "ink": await DecideInk(user, x.Id, decision, token); break;
            case "maintenance": await DecideMaintenance(user, x.Id, decision, token); break;
            case "movement": await DecideMovement(user, x.Id, new ItamMovementDecision(x.Approve, decision.Reason, null), token); break;
            case "disposal": await DecideDisposal(user, x.Id, decision, token); break;
        }
        return await QuickDecisionInfo(user, k, x.Id, stage, token);
    }

    /// <summary>The Approve and Reject links an approval email carries.</summary>
    (string Approve, string Reject) DecisionLinks(string kind, int id, string stage)
    {
        var b = $"{SiteUrl}/decide.html?k={kind}&id={id}&s={stage}";
        return (b + "&a=approve", b + "&a=reject");
    }
}
