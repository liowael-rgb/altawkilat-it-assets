using System.Data;
using System.Data.Common;

namespace Itam.App;

/// <summary>Emails (IT-EM templates and the owner's notification manifest; layout as CMS). Each action writes its email to the
/// outbox in its own transaction; the mail sender sends it after commit. Approval emails go to the approver and to the
/// approver's backup during leave; To and CC are de-duplicated and the person who acted is not asked to act on their own
/// record. A missing recipient never loses the email: it is kept as "No recipient" and the IT Manager is told (IT-EM-56).
/// Links open the record's page in IT Assets (sign-in required); nothing is approved by opening a link.</summary>
public sealed partial class ItamStore
{
    public string SiteUrl => LabelBaseUrl.EndsWith("/a/", StringComparison.Ordinal) ? LabelBaseUrl[..^3] : LabelBaseUrl.TrimEnd('/');

    static readonly Dictionary<string, string> MailNames = new()
    {
        ["IT-EM-01"] = "Delivery note to sign", ["IT-EM-02"] = "Receipt note", ["IT-EM-04"] = "Signed copy reminder", ["IT-EM-05"] = "Signed copy overdue",
        ["IT-EM-09"] = "Maintenance order approval", ["IT-EM-10"] = "Disposal approval", ["IT-EM-11"] = "Collect assets from leaver", ["IT-EM-12"] = "Weekly summary",
        ["IT-EM-13"] = "Remove computer from AD and Endpoint Central", ["IT-EM-15"] = "Procurement request decision", ["IT-EM-18"] = "New device request",
        ["IT-EM-19"] = "Gate pass approval", ["IT-EM-20"] = "Gate pass decision", ["IT-EM-21"] = "Gate pass return reminder", ["IT-EM-23"] = "Remove IP phone extension from CUCM",
        ["IT-EM-24"] = "AD computer comparison report", ["IT-EM-25"] = "You are a backup", ["IT-EM-26"] = "Cost approval by the manager", ["IT-EM-27"] = "IT accessory handed over",
        ["IT-EM-28"] = "IT accessory cost approval by the manager", ["IT-EM-29"] = "Ink request approval", ["IT-EM-30"] = "New ink request",
        ["IT-EM-31"] = "Device request approval by the manager", ["IT-EM-32"] = "Transfer dispatched - receipt required", ["IT-EM-33"] = "Transfer partially received",
        ["IT-EM-34"] = "Transfer completed", ["IT-EM-35"] = "Transfer discrepancy reported", ["IT-EM-36"] = "Transfer discrepancy resolved", ["IT-EM-37"] = "Transfer receipt overdue",
        ["IT-EM-38"] = "Transfer cancelled or dispatch validity expired", ["IT-EM-41"] = "Asset loss reported", ["IT-EM-42"] = "Loss investigation assigned",
        ["IT-EM-43"] = "Loss investigation ready for decision", ["IT-EM-44"] = "Loss decision and required action", ["IT-EM-45"] = "Loss responsibility clearance outcome",
        ["IT-EM-46"] = "Lost asset recovered", ["IT-EM-47"] = "Disposal decision", ["IT-EM-48"] = "Approved disposal awaiting completion", ["IT-EM-49"] = "Disposal completion overdue",
        ["IT-EM-50"] = "Disposal completed", ["IT-EM-51"] = "Maintenance order decision", ["IT-EM-52"] = "Maintenance supplier return overdue", ["IT-EM-53"] = "Maintenance repair outcome",
        ["IT-EM-54"] = "Regional accessory pricing assignment", ["IT-EM-55"] = "Approved accessory - purchase action", ["IT-EM-56"] = "Workflow routing or notification failure",
        ["IT-EM-57"] = "Transfer ready for dispatch", ["IT-EM-58"] = "Request allocated - install and deliver", ["IT-EM-59"] = "Ink decision - fulfilment action",
        ["IT-EM-61"] = "Pending workflow action reminder", ["IT-EM-62"] = "Technical assessment ready", ["IT-EM-63"] = "Loss case action overdue",
        ["IT-EM-64"] = "Disable retained AD user after clearance"
    };

    sealed record Rcpt(Guid Id, string Name, string? Email);
    static async Task<List<Rcpt>> People(DbConnection c, DbTransaction? tx, params Guid?[] ids)
    {
        var want = ids.OfType<Guid>().Distinct().ToList(); if (want.Count == 0) return [];
        return await Rows(c, tx, $"SELECT employee_id,display_name,email FROM itam_access.employee WHERE employee_id IN({string.Join(",", want.Select((_, i) => "@p" + i))})",
            r => new Rcpt(r.GetGuid(0), r.GetString(1), S(r, 2)), token: default, [.. want.Select((g, i) => ("@p" + i, (object?)g))]);
    }
    /// <summary>The holders of the roles; IT Support Specialists and Ink Officers only of the region when one is given.</summary>
    static async Task<List<Rcpt>> RoleHolders(DbConnection c, DbTransaction? tx, string? region, params string[] roles)
        => await Rows(c, tx, $"SELECT DISTINCT e.employee_id,e.display_name,e.email FROM itam_access.role_assignment r JOIN itam_access.employee e ON e.employee_id=r.employee_id " +
            $"WHERE e.enabled=1 AND r.role_code IN({string.Join(",", roles.Select((_, i) => "@r" + i))}) AND (@g IS NULL OR r.region_code IS NULL OR r.region_code=@g)",
            r => new Rcpt(r.GetGuid(0), r.GetString(1), S(r, 2)), default, [("@g", region), .. roles.Select((x, i) => ("@r" + i, (object?)x))]);
    /// <summary>The people standing in today for any of these people on leave (their backups receive the approval emails).</summary>
    static async Task<List<Rcpt>> Backups(DbConnection c, DbTransaction? tx, IEnumerable<Rcpt> people)
    {
        var ids = people.Select(p => p.Id).Distinct().ToList(); if (ids.Count == 0) return [];
        return await Rows(c, tx, $"SELECT DISTINCT e.employee_id,e.display_name,e.email FROM itam_access.leave_backup b JOIN itam_access.employee e ON e.employee_id=b.backup_employee_id " +
            $"WHERE b.cancelled_at_utc IS NULL AND CAST(DATEADD(hour,3,SYSUTCDATETIME()) AS date) BETWEEN b.leave_from AND b.leave_to AND b.person_id IN({string.Join(",", ids.Select((_, i) => "@p" + i))})",
            r => new Rcpt(r.GetGuid(0), r.GetString(1), S(r, 2)), default, [.. ids.Select((g, i) => ("@p" + i, (object?)g))]);
    }
    static async Task<List<Rcpt>> LineManager(DbConnection c, DbTransaction? tx, Guid? employee)
        => employee is Guid e ? await Rows(c, tx, "SELECT m.employee_id,m.display_name,m.email FROM itam_access.employee e JOIN itam_access.employee m ON m.employee_id=e.manager_id WHERE e.employee_id=@e",
            r => new Rcpt(r.GetGuid(0), r.GetString(1), S(r, 2)), default, ("@e", e)) : [];
    static List<Rcpt> L(params IEnumerable<Rcpt>[] lists) => lists.SelectMany(x => x).ToList();

    /// <summary>Writes one email to the outbox (once per event key). Approval emails also go to the approvers' backups on leave.</summary>
    async Task Notify(DbConnection c, DbTransaction? tx, string code, string key, string reference, List<Rcpt> to, List<Rcpt> cc, string action,
        (string, string?)[] details, string path, string linkText, Guid? actor, (string, string)[]? sections = null, bool approval = false, (string Kind, int Id, string Stage)? decide = null)
    {
        if (approval) { to = L(to, await Backups(c, tx, to)); to = to.Where(p => p.Id != actor).ToList(); }
        var toList = to.GroupBy(p => p.Id).Select(g => g.First()).ToList();
        var ccList = cc.GroupBy(p => p.Id).Select(g => g.First()).Where(p => toList.All(t => t.Id != p.Id)).ToList();
        var actorName = actor is Guid a ? await Scalar(c, tx, "SELECT display_name FROM itam_access.employee WHERE employee_id=@a", default, ("@a", a)) as string : null;
        var links = decide is { } dl ? DecisionLinks(dl.Kind, dl.Id, dl.Stage) : default;
        var view = new ItamMailView(code, MailNames[code], reference, toList.Count == 1 ? toList[0].Name : "Colleagues", action, details, sections ?? [], SiteUrl + path, linkText, actorName,
            decide is null ? null : links.Approve, decide is null ? null : links.Reject);
        var toMails = toList.Where(p => !string.IsNullOrWhiteSpace(p.Email)).Select(p => p.Email!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var ccMails = ccList.Where(p => !string.IsNullOrWhiteSpace(p.Email)).Select(p => p.Email!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Where(m => !toMails.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        var status = toMails.Count == 0 ? "NoRecipient" : "Queued";
        var inserted = await Exec(c, tx, "IF NOT EXISTS(SELECT 1 FROM itam_data.mail_outbox WHERE event_key=@k) INSERT itam_data.mail_outbox(template,event_key,record_ref,to_list,cc_list,recipients,subject,body_html,status) " +
            "VALUES(@t,@k,@r,@to,@cc,@who,@s,@b,@st)", default, ("@t", code), ("@k", key), ("@r", reference), ("@to", string.Join("; ", toMails)), ("@cc", ccMails.Count == 0 ? null : string.Join("; ", ccMails)),
            ("@who", string.Join("; ", toList.Select(p => p.Name)) + (ccList.Count == 0 ? "" : " | CC " + string.Join("; ", ccList.Select(p => p.Name)))),
            ("@s", $"[IT Assets] {MailNames[code]} - {reference}"), ("@b", ItamMailLayout.Html(view, null)), ("@st", status));
        // Nobody to send to (no manager in AD, no email address): the IT Manager is told, never by recursion of the same failure.
        if (inserted > 0 && status == "NoRecipient" && code != "IT-EM-56")
            await Notify(c, tx, "IT-EM-56", "IT-EM-56|" + key, reference, await RoleHolders(c, tx, null, ItamRoles.ItManager), [],
                $"The email \"{MailNames[code]}\" for {reference} has no recipient with an email address ({(toList.Count == 0 ? "nobody is assigned" : string.Join(", ", toList.Select(p => p.Name)))}). Check the record and Active Directory.",
                [("Email", code + " " + MailNames[code]), ("Record", reference)], path, "Open the record", null);
    }

    // ---- Device and accessory requests ---------------------------------------------------------------------------------------------
    sealed record ReqMail(string No, Guid Employee, string EmployeeName, Guid RequestedBy, Guid? Manager, string Type, string Description, string? Source, Guid? Assigned, string? Department, string? JobTitle, string? Branch, string? Replaced, int Id, string? RejectReason);
    static async Task<ReqMail> RequestMail(DbConnection c, DbTransaction tx, int id)
        => (await Rows(c, tx, "SELECT q.request_no,q.employee_id,e.display_name,q.requested_by,q.manager_id,q.request_type,q.description,q.source,q.assigned_to,e.department,e.job_title,e.branch,a.asset_no,q.request_id,q.reject_reason " +
            "FROM itam_data.request q JOIN itam_access.employee e ON e.employee_id=q.employee_id LEFT JOIN itam_data.asset a ON a.asset_id=q.replaced_asset_id WHERE q.request_id=@i",
            r => new ReqMail(r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), G0(r, 4), r.GetString(5), r.GetString(6), S(r, 7), G0(r, 8), S(r, 9), S(r, 10), S(r, 11), S(r, 12), r.GetInt32(13), S(r, 14)), default, ("@i", id))).Single();
    static (string, string?)[] ReqDetails(ReqMail q) => [("Request No.", q.No), ("Employee", q.EmployeeName), ("Job title", q.JobTitle), ("Department", q.Department), ("Branch", q.Branch),
        ("Request type", q.Type == "Replacement" ? "Replacement" + (q.Replaced is null ? "" : " of " + q.Replaced) : "New device")];

    internal async Task MailRequest(DbConnection c, DbTransaction tx, int id, string what, Guid actor)
    {
        var q = await RequestMail(c, tx, id); var path = "/request.html?id=" + id; var details = ReqDetails(q);
        var employee = await People(c, tx, q.Employee, q.RequestedBy); var manager = await People(c, tx, q.Manager); var officers = await RoleHolders(c, tx, null, ItamRoles.AssetsOfficer);
        (string, string)[] desc = [("Request details", q.Description)];
        switch (what)
        {
            case "created": await Notify(c, tx, "IT-EM-31", $"IT-EM-31|{q.No}", q.No, manager, employee, $"{q.EmployeeName} has requested an IT device; IT starts only after your approval.", details, path, "Open the request to approve or reject", actor, desc, approval: true, decide: ("request", id, "WaitingRequestApproval")); break;
            case "approved": await Notify(c, tx, "IT-EM-18", $"IT-EM-18|{q.No}", q.No, officers, L(employee, manager), $"The manager approved {q.No}: choose the source (stock, new device or IT accessory).", details, path, "Open the request", actor, desc); break;
            case "rejected": case "cost-rejected": case "expired":
                await Notify(c, tx, "IT-EM-15", $"IT-EM-15|{q.No}|{what}", q.No, employee, L(manager, what == "cost-rejected" ? officers : []),
                    what == "expired" ? $"{q.No} was cancelled automatically: the manager did not decide within {ApprovalDays} days." : $"{q.No} was rejected{(q.RejectReason is null ? "." : ": " + q.RejectReason)}", details, path, "Open the request", actor); break;
            case "stock": await Notify(c, tx, "IT-EM-58", $"IT-EM-58|{q.No}", q.No, L(await People(c, tx, q.Assigned), q.Assigned is null ? officers : []), L(officers, employee), $"{q.No} is allocated from stock: install and deliver it to {q.EmployeeName}.", details, path, "Open the request", actor); break;
            case "pricing": await Notify(c, tx, "IT-EM-54", $"IT-EM-54|{q.No}", q.No, await People(c, tx, q.Assigned), officers, $"Price the IT accessory of {q.No} with a local shop, for the manager's cost approval.", details, path, "Open the request", actor, desc); break;
            case "cost-new": await Notify(c, tx, "IT-EM-26", $"IT-EM-26|{q.No}", q.No, manager, employee, $"IT has priced the new device for {q.No}; please approve or reject the cost.", details, path, "Open the cost table to approve or reject", actor, approval: true, decide: ("request", id, "WaitingCostApproval")); break;
            case "cost-accessory": await Notify(c, tx, "IT-EM-28", $"IT-EM-28|{q.No}", q.No, manager, L(employee, officers), $"IT has priced the IT accessory for {q.No}; please approve or reject the cost.", details, path, "Open the cost table to approve or reject", actor, approval: true, decide: ("request", id, "WaitingCostApproval")); break;
            case "cost-approved":
                if (q.Source == "Accessory") await Notify(c, tx, "IT-EM-55", $"IT-EM-55|{q.No}", q.No, await People(c, tx, q.Assigned), L(officers, employee, manager), $"The cost of {q.No} is approved: buy the accessory (petty cash, with the receipt) and hand it over.", details, path, "Open the request", actor);
                else await Notify(c, tx, "IT-EM-15", $"IT-EM-15|{q.No}|cost-approved", q.No, officers, L(employee, manager), $"The manager approved the cost of {q.No}: order the device.", details, path, "Open the request", actor);
                break;
            case "fulfilled": if (q.Source == "Accessory") await Notify(c, tx, "IT-EM-27", $"IT-EM-27|{q.No}", q.No, employee, L(manager, officers), $"The IT accessory of {q.No} was bought and handed over.", details, path, "Open the request", actor); break;
        }
    }

    // ---- Ink ---------------------------------------------------------------------------------------------------------------------------
    internal async Task MailInk(DbConnection c, DbTransaction tx, int id, string what, Guid actor)
    {
        var k = (await Rows(c, tx, "SELECT q.request_no,q.employee_id,e.display_name,q.region_code,q.manager_id,q.toner,q.quantity,COALESCE(a.asset_no,N'S/N '+q.new_serial),q.status,q.reject_reason,e.department " +
            "FROM itam_data.ink_request q JOIN itam_access.employee e ON e.employee_id=q.employee_id LEFT JOIN itam_data.asset a ON a.asset_id=q.printer_asset_id WHERE q.request_id=@i",
            r => (No: r.GetString(0), Emp: r.GetGuid(1), Name: r.GetString(2), Region: S(r, 3), Manager: G0(r, 4), Toner: r.GetString(5), Qty: r.GetInt32(6), Printer: r.GetString(7), Status: r.GetString(8), Reason: S(r, 9), Dept: S(r, 10)), default, ("@i", id))).Single();
        var handlers = L(await RoleHolders(c, tx, k.Region, ItamRoles.InkOfficer), await RoleHolders(c, tx, null, ItamRoles.AssetsOfficer));
        (string, string?)[] details = [("Ink request", k.No), ("Employee", k.Name), ("Department", k.Dept), ("Region", k.Region), ("Printer", k.Printer), ("Toner", $"{k.Qty} × {k.Toner}")];
        var employee = await People(c, tx, k.Emp);
        switch (what)
        {
            case "created": await Notify(c, tx, "IT-EM-30", $"IT-EM-30|{k.No}", k.No, handlers, [], $"{k.Name} asked for ink: check the printer and send the request for approval.", details, "/ink.html", "Open the ink requests", actor); break;
            case "sent": await Notify(c, tx, "IT-EM-29", $"IT-EM-29|{k.No}|{DateTime.UtcNow:yyyyMMddHHmm}", k.No, await People(c, tx, k.Manager), employee, $"{k.Name} asked for ink; please approve or reject.", details, "/ink.html", "Open the ink request to approve or reject", actor, approval: true, decide: ("ink", id, "WaitingApproval")); break;
            case "decided": await Notify(c, tx, "IT-EM-59", $"IT-EM-59|{k.No}", k.No, handlers, L(employee, await People(c, tx, k.Manager)),
                k.Status == "Approved" ? $"{k.No} is approved: deliver the toner from the shelf or order it." : $"{k.No} was rejected{(k.Reason is null ? "." : ": " + k.Reason)}", details, "/ink.html", "Open the ink requests", actor); break;
        }
    }

    // ---- Handover: delivery, receipt, infrastructure tasks, technical assessment -----------------------------------------------------
    internal async Task MailDelivery(DbConnection c, DbTransaction tx, int deliveryId, string what, Guid actor)
    {
        var d = (await Rows(c, tx, "SELECT d.delivery_no,d.employee_id,e.display_name,a.asset_no,t.name_en,CONCAT(mf.name_en,' ',mo.name_en),COALESCE(a.serial_no,a.service_tag),d.computer_name,d.delivered_on,d.received_at_utc,d.receipt_condition,a.asset_id,d.issued_by,g.region_code,a.type_code " +
            "FROM itam_data.delivery d JOIN itam_access.employee e ON e.employee_id=d.employee_id JOIN itam_data.asset a ON a.asset_id=d.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code JOIN itam_ref.location g ON g.location_id=a.location_id " +
            "LEFT JOIN itam_ref.manufacturer mf ON mf.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id WHERE d.delivery_id=@d",
            r => (No: r.GetString(0), Emp: r.GetGuid(1), Name: r.GetString(2), Asset: r.GetString(3), Type: r.GetString(4), Model: S(r, 5)?.Trim(), Serial: S(r, 6), Computer: S(r, 7), On: Day(r, 8),
                Back: r.IsDBNull(9) ? (DateTime?)null : Utc(r, 9), Cond: S(r, 10), AssetId: r.GetInt32(11), Issuer: r.GetGuid(12), Region: r.GetString(13), TypeCode: r.GetString(14)), default, ("@d", deliveryId))).Single();
        (string, string?)[] details = [("Asset No.", d.Asset), ("Product", $"{d.Type} {d.Model}".Trim()), ("Serial / service tag", d.Serial), ("Computer name", d.Computer), ("Employee", d.Name),
            (what == "receipt" ? "Receipt note" : "Delivery note", what == "receipt" ? d.No + "-R" : d.No), (what == "receipt" ? "Received" : "Delivered", what == "receipt" ? ItamMailLayout.When(d.Back) : ItamMailLayout.Date(d.On)), ("Condition", what == "receipt" ? d.Cond : null)];
        var employee = await People(c, tx, d.Emp); var manager = await LineManager(c, tx, d.Emp); var issuer = await People(c, tx, d.Issuer); var path = "/asset.html?id=" + d.AssetId;
        switch (what)
        {
            case "delivered": await Notify(c, tx, "IT-EM-01", $"IT-EM-01|{d.No}", d.No, employee, manager, $"{d.Asset} is delivered to you on {d.No}. Please sign the printed delivery note by hand; IT keeps the signed copy.", details, path, "Open the asset", actor); break;
            case "receipt": await Notify(c, tx, "IT-EM-02", $"IT-EM-02|{d.No}", d.No + "-R", employee, manager, $"IT received {d.Asset} back from you on receipt note {d.No}-R.", details, path, "Open the asset", actor); break;
            case "tasks":
                if (d.TypeCode == "PC") await Notify(c, tx, "IT-EM-13", $"IT-EM-13|{d.No}", d.Asset, await RoleHolders(c, tx, null, ItamRoles.SystemAdministrator), issuer, $"{d.Asset}{(d.Computer is null ? "" : " (" + d.Computer + ")")} came back: remove the computer from Active Directory and Endpoint Central, then mark the task done.", details, "/tasks.html", "Open the tasks", actor);
                if (d.TypeCode == "IP") await Notify(c, tx, "IT-EM-23", $"IT-EM-23|{d.No}", d.Asset, await RoleHolders(c, tx, null, ItamRoles.VoiceAdministrator), issuer, $"IP phone {d.Asset} came back: remove its extension from CUCM, then mark the task done.", details, "/tasks.html", "Open the tasks", actor);
                break;
            case "assessed": await Notify(c, tx, "IT-EM-62", $"IT-EM-62|{d.No}", d.Asset, L(await RoleHolders(c, tx, d.Region, ItamRoles.SupportSpecialist), await RoleHolders(c, tx, null, ItamRoles.AssetsOfficer)), [],
                $"The technical assessment TA-{d.No} of {d.Asset} is recorded.", details, path, "Open the asset", actor); break;
        }
    }

    /// <summary>A sold computer or IP phone (no delivery for a sale from stock): AD or CUCM clean-up.</summary>
    internal async Task MailSaleTasks(DbConnection c, DbTransaction tx, int assetId, string saleNo, Guid actor)
    {
        var a = (await Rows(c, tx, "SELECT asset_no,type_code FROM itam_data.asset WHERE asset_id=@a", r => (No: r.GetString(0), Type: r.GetString(1)), default, ("@a", assetId))).Single();
        if (a.Type is not ("PC" or "IP")) return;
        await Notify(c, tx, a.Type == "PC" ? "IT-EM-13" : "IT-EM-23", $"{(a.Type == "PC" ? "IT-EM-13" : "IT-EM-23")}|{saleNo}", a.No,
            await RoleHolders(c, tx, null, a.Type == "PC" ? ItamRoles.SystemAdministrator : ItamRoles.VoiceAdministrator), await People(c, tx, actor),
            a.Type == "PC" ? $"{a.No} was sold ({saleNo}): remove the computer from Active Directory and Endpoint Central." : $"IP phone {a.No} was sold ({saleNo}): remove its extension from CUCM.",
            [("Asset No.", a.No), ("Sale", saleNo)], "/tasks.html", "Open the tasks", actor);
    }

    // ---- Maintenance -------------------------------------------------------------------------------------------------------------------
    internal async Task MailMaintenance(DbConnection c, DbTransaction tx, int id, string what, Guid actor)
    {
        var o = (await Rows(c, tx, "SELECT m.order_no,a.asset_no,su.name_en,m.cost,m.details,m.status,m.created_by,m.rejection_reason,m.outcome,m.repair_details,m.final_cost,g.region_code,m.expected_return " +
            "FROM itam_data.maintenance_order m JOIN itam_data.asset a ON a.asset_id=m.asset_id JOIN itam_ref.supplier su ON su.supplier_id=m.supplier_id JOIN itam_ref.location g ON g.location_id=a.location_id WHERE m.order_id=@i",
            r => (No: r.GetString(0), Asset: r.GetString(1), Supplier: r.GetString(2), Cost: r.GetDecimal(3), Details: r.GetString(4), Status: r.GetString(5), By: r.GetGuid(6), Reason: S(r, 7), Outcome: S(r, 8), Repair: S(r, 9), Final: r.IsDBNull(10) ? (decimal?)null : r.GetDecimal(10), Region: r.GetString(11), Expected: Day(r, 12)), default, ("@i", id))).Single();
        (string, string?)[] details = [("Order", o.No), ("Asset No.", o.Asset), ("Supplier", o.Supplier), ("Quotation cost", ItamMailLayout.Sar(o.Cost)), ("Expected return", ItamMailLayout.Date(o.Expected)), ("Final cost", o.Final is null ? null : ItamMailLayout.Sar(o.Final))];
        var creator = await People(c, tx, o.By); var officers = await RoleHolders(c, tx, null, ItamRoles.AssetsOfficer); var regional = await RoleHolders(c, tx, o.Region, ItamRoles.SupportSpecialist);
        switch (what)
        {
            case "created": await Notify(c, tx, "IT-EM-09", $"IT-EM-09|{o.No}", o.No, await RoleHolders(c, tx, null, ItamRoles.ItManager, ItamRoles.TeamLeader, ItamRoles.SystemAdministrator), creator,
                $"Maintenance order {o.No} for {o.Asset} waits for approval, with the supplier's quotation.", details, "/maintenance.html", "Open the order to approve or reject", actor, [("Maintenance details", o.Details)], approval: true, decide: ("maintenance", id, "WaitingApproval")); break;
            case "decided": await Notify(c, tx, "IT-EM-51", $"IT-EM-51|{o.No}", o.No, L(officers, creator), regional,
                o.Status == "Approved" ? $"{o.No} is approved: send {o.Asset} to {o.Supplier} and record the dispatch." : $"{o.No} was rejected{(o.Reason is null ? "." : ": " + o.Reason)}", details, "/maintenance.html", "Open the maintenance orders", actor); break;
            case "returned": await Notify(c, tx, "IT-EM-53", $"IT-EM-53|{o.No}", o.No, L(officers, creator), regional, $"{o.Asset} is back from {o.Supplier}: {(o.Outcome == "Repaired" ? "repaired" : "not repairable")}.", details, "/maintenance.html", "Open the maintenance orders", actor, [("Repair details", o.Repair ?? "")]); break;
        }
    }

    // ---- Movements -----------------------------------------------------------------------------------------------------------------
    internal async Task MailMovement(DbConnection c, DbTransaction tx, int id, string what, Guid actor)
    {
        var m = (await Rows(c, tx, "SELECT m.movement_no,m.kind,m.purpose,fg.name_en,tg.name_en,m.destination,m.requested_by,m.employee_id,m.sender_id,m.receiver_id,m.status,m.reject_reason,m.cancelled_reason,m.exit_date,m.valid_until,m.expected_date,fg.region_code,tg.region_code," +
            "(SELECT STRING_AGG(a.asset_no,N', ') FROM itam_data.movement_line l JOIN itam_data.asset a ON a.asset_id=l.asset_id WHERE l.movement_id=m.movement_id AND l.state<>N'Cancelled') " +
            "FROM itam_data.movement m JOIN itam_ref.location fg ON fg.location_id=m.from_location_id LEFT JOIN itam_ref.location tg ON tg.location_id=m.to_location_id WHERE m.movement_id=@i",
            r => (No: r.GetString(0), Kind: r.GetString(1), Purpose: r.GetString(2), From: r.GetString(3), To: S(r, 4) ?? S(r, 5), Req: r.GetGuid(6), Emp: G0(r, 7), Sender: G0(r, 8), Receiver: G0(r, 9), Status: r.GetString(10),
                Reject: S(r, 11), Cancel: S(r, 12), Exit: Day(r, 13), Valid: Day(r, 14), Expected: Day(r, 15), FromRegion: r.GetString(16), ToRegion: S(r, 17), Assets: S(r, 18)), default, ("@i", id))).Single();
        (string, string?)[] details = [(m.Kind == "Warehouse" ? "Warehouse transfer" : "Gate pass", m.No), ("Kind", m.Kind switch { "Branch" => "Branch transfer", "Exit" => "Exit pass", _ => "Warehouse transfer" }),
            ("Purpose", m.Purpose), ("From", m.From), ("To", m.To), ("Assets", m.Assets), ("Exit date", ItamMailLayout.Date(m.Exit)), ("Valid until", ItamMailLayout.Date(m.Valid)), ("Expected receipt", m.Expected is null ? null : ItamMailLayout.Date(m.Expected))];
        var path = "/movement.html?id=" + id;
        var requester = await People(c, tx, m.Req, m.Emp); var sender = await People(c, tx, m.Sender); var receiver = await People(c, tx, m.Receiver);
        var itAround = L(await RoleHolders(c, tx, m.FromRegion, ItamRoles.SupportSpecialist), m.ToRegion is null ? [] : await RoleHolders(c, tx, m.ToRegion, ItamRoles.SupportSpecialist),
            await RoleHolders(c, tx, null, ItamRoles.AssetsOfficer, ItamRoles.TeamLeader));
        switch (what)
        {
            case "created" when m.Kind != "Warehouse": await Notify(c, tx, "IT-EM-19", $"IT-EM-19|{m.No}", m.No, await RoleHolders(c, tx, null, ItamRoles.TeamLeader, ItamRoles.SystemAdministrator, ItamRoles.ItManager), requester,
                $"Gate pass {m.No} waits for approval.", details, path, "Open the gate pass to approve or reject", actor, approval: true, decide: ("movement", id, "Submitted")); break;
            case "created": case "approved":
                if (what == "approved") await Notify(c, tx, "IT-EM-20", $"IT-EM-20|{m.No}", m.No, requester, await LineManager(c, tx, m.Emp ?? m.Req), $"{m.No} is approved: print the pass for Security and dispatch within its validity.", details, path, "Open the gate pass", actor);
                if (m.Kind != "Exit" || what == "approved")
                    await Notify(c, tx, "IT-EM-57", $"IT-EM-57|{m.No}", m.No, sender.Count > 0 ? sender : await RoleHolders(c, tx, m.FromRegion, ItamRoles.SupportSpecialist), L(receiver, await RoleHolders(c, tx, null, ItamRoles.AssetsOfficer)),
                        $"{m.No} is ready for dispatch: the pass is valid until {ItamMailLayout.Date(m.Valid)}.", details, path, "Open the movement", actor);
                break;
            case "rejected": await Notify(c, tx, "IT-EM-20", $"IT-EM-20|{m.No}", m.No, requester, await LineManager(c, tx, m.Emp ?? m.Req), $"{m.No} was rejected{(m.Reject is null ? "." : ": " + m.Reject)}", details, path, "Open the gate pass", actor); break;
            case "dispatched" when m.Kind != "Exit": await Notify(c, tx, "IT-EM-32", $"IT-EM-32|{m.No}|{DateTime.UtcNow:yyyyMMddHHmm}", m.No, receiver, L(sender, itAround), $"{m.No} is on its way to {m.To}: as the named receiver, confirm each asset when it arrives.", details, path, "Open the transfer to confirm the receipt", actor); break;
            case "received":
                if (m.Status == "ReceiptDisputed") await Notify(c, tx, "IT-EM-35", $"IT-EM-35|{m.No}|{DateTime.UtcNow:yyyyMMddHHmm}", m.No, await RoleHolders(c, tx, null, ItamRoles.TeamLeader, ItamRoles.SystemAdministrator, ItamRoles.ItManager), L(sender, receiver, itAround),
                    $"A discrepancy is reported on {m.No} (missing, damaged or incorrect assets): resolve each line with its evidence.", details, path, "Open the transfer", actor);
                else if (m.Status is "Completed" or "ClosedWithExceptions") await Notify(c, tx, "IT-EM-34", $"IT-EM-34|{m.No}", m.No, L(sender, receiver), itAround, $"{m.No} is {(m.Status == "Completed" ? "completed" : "closed with exceptions")}.", details, path, "Open the transfer", actor);
                else await Notify(c, tx, "IT-EM-33", $"IT-EM-33|{m.No}|{DateTime.UtcNow:yyyyMMddHHmm}", m.No, L(sender, receiver), itAround, $"{m.No} is partially received; the other assets are still outstanding.", details, path, "Open the transfer", actor);
                break;
            case "resolved": await Notify(c, tx, "IT-EM-36", $"IT-EM-36|{m.No}|{DateTime.UtcNow:yyyyMMddHHmm}", m.No, L(sender, receiver), itAround, $"A discrepancy on {m.No} was resolved.", details, path, "Open the transfer", actor);
                if (m.Status is "Completed" or "ClosedWithExceptions") await Notify(c, tx, "IT-EM-34", $"IT-EM-34|{m.No}", m.No, L(sender, receiver), itAround, $"{m.No} is {(m.Status == "Completed" ? "completed" : "closed with exceptions")}.", details, path, "Open the transfer", actor);
                break;
            case "cancelled": case "expired": await Notify(c, tx, "IT-EM-38", $"IT-EM-38|{m.No}", m.No, L(requester, sender, receiver), itAround,
                what == "expired" ? $"{m.No} expired: it was not dispatched within its validity. Ask for a new pass if it is still needed." : $"{m.No} was cancelled{(m.Cancel is null ? "." : ": " + m.Cancel)}", details, path, "Open the movement", actor); break;
        }
    }

    // ---- End of life -------------------------------------------------------------------------------------------------------------------
    internal async Task MailDisposal(DbConnection c, DbTransaction tx, int id, string what, Guid actor)
    {
        var d = (await Rows(c, tx, "SELECT x.disposal_no,a.asset_no,t.name_en,x.reason,x.status,x.requested_by,x.reject_reason,x.region_code,x.completed_on,x.method FROM itam_data.disposal x JOIN itam_data.asset a ON a.asset_id=x.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code WHERE x.disposal_id=@i",
            r => (No: r.GetString(0), Asset: r.GetString(1), Type: r.GetString(2), Reason: r.GetString(3), Status: r.GetString(4), By: r.GetGuid(5), Reject: S(r, 6), Region: r.GetString(7), On: Day(r, 8), Method: S(r, 9)), default, ("@i", id))).Single();
        (string, string?)[] details = [("Disposal", d.No), ("Asset No.", d.Asset), ("Type", d.Type), ("Reason", d.Reason), ("Disposed on", d.On is null ? null : ItamMailLayout.Date(d.On))];
        var requester = await People(c, tx, d.By); var officers = await RoleHolders(c, tx, null, ItamRoles.AssetsOfficer); var managers = await RoleHolders(c, tx, null, ItamRoles.ItManager); var regional = await RoleHolders(c, tx, d.Region, ItamRoles.SupportSpecialist);
        switch (what)
        {
            case "created": await Notify(c, tx, "IT-EM-10", $"IT-EM-10|{d.No}", d.No, managers, requester, $"Disposal {d.No} of {d.Asset} waits for your approval. Approval alone does not dispose.", details, "/disposals.html", "Open the disposals to approve or reject", actor, approval: true, decide: ("disposal", id, "WaitingApproval")); break;
            case "decided":
                await Notify(c, tx, "IT-EM-47", $"IT-EM-47|{d.No}", d.No, L(requester, officers), L(regional, managers), d.Status == "Approved" ? $"{d.No} is approved." : $"{d.No} was rejected{(d.Reject is null ? "." : ": " + d.Reject)} The asset is back In Store.", details, "/disposals.html", "Open the disposals", actor);
                if (d.Status == "Approved") await Notify(c, tx, "IT-EM-48", $"IT-EM-48|{d.No}", d.No, officers, L(regional, managers), $"{d.No} is approved and waits for its completion: record the date, method and evidence when {d.Asset} is disposed.", details, "/disposals.html", "Open the disposals", actor);
                break;
            case "completed": await Notify(c, tx, "IT-EM-50", $"IT-EM-50|{d.No}", d.No, L(requester, officers, regional), managers, $"{d.Asset} is disposed ({d.No}).", details, "/disposals.html", "Open the disposals", actor); break;
        }
    }

    internal async Task MailLoss(DbConnection c, DbTransaction tx, int id, string what, Guid actor)
    {
        var l = (await Rows(c, tx, "SELECT l.case_no,a.asset_no,t.name_en,l.region_code,l.reported_by,l.custodian_id,l.investigator_id,l.status,l.responsibility,l.decision,l.settlement_amount,l.discovered_on,l.last_location " +
            "FROM itam_data.loss_case l JOIN itam_data.asset a ON a.asset_id=l.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code WHERE l.case_id=@i",
            r => (No: r.GetString(0), Asset: r.GetString(1), Type: r.GetString(2), Region: r.GetString(3), By: r.GetGuid(4), Cu: G0(r, 5), Iv: G0(r, 6), Status: r.GetString(7), Resp: r.GetString(8), Decision: S(r, 9), Amount: r.IsDBNull(10) ? (decimal?)null : r.GetDecimal(10), On: Day(r, 11), Last: r.GetString(12)), default, ("@i", id))).Single();
        // Investigation details stay in IT Assets: the email gives the asset, the state and the outcome only.
        (string, string?)[] details = [("Loss case", l.No), ("Asset No.", l.Asset), ("Type", l.Type), ("Discovered on", ItamMailLayout.Date(l.On)), ("Last known location", l.Last), ("Responsibility", ResponsibilityLabel(l.Resp))];
        var path = "/loss.html?id=" + id; var managers = await RoleHolders(c, tx, null, ItamRoles.ItManager); var officers = await RoleHolders(c, tx, null, ItamRoles.AssetsOfficer);
        var custodian = await People(c, tx, l.Cu); var investigator = await People(c, tx, l.Iv);
        switch (what)
        {
            case "reported": await Notify(c, tx, "IT-EM-41", $"IT-EM-41|{l.No}", l.No, L(await RoleHolders(c, tx, l.Region, ItamRoles.SupportSpecialist), await RoleHolders(c, tx, null, ItamRoles.TeamLeader, ItamRoles.AssetsOfficer, ItamRoles.ItManager)),
                L(await People(c, tx, l.By), await LineManager(c, tx, l.Cu)), $"{l.Asset} is reported lost ({l.No}). The IT Manager assigns the investigator; the asset stays in the employee's custody until the decision.", details, path, "Open the loss case", actor); break;
            case "assigned": await Notify(c, tx, "IT-EM-42", $"IT-EM-42|{l.No}|{l.Iv}", l.No, investigator, managers, $"You are the investigator of {l.No}: record the findings and your recommendation.", details, path, "Open the loss case", actor); break;
            case "findings": await Notify(c, tx, "IT-EM-43", $"IT-EM-43|{l.No}", l.No, managers, investigator, $"The investigation of {l.No} is ready for your decision.", details, path, "Open the loss case to decide", actor, approval: true); break;
            case "decided": await Notify(c, tx, "IT-EM-44", $"IT-EM-44|{l.No}", l.No, L(custodian, l.Decision == "Settlement" ? officers : []), managers,
                l.Decision == "Settlement" ? $"The decision on {l.No}: a settlement of {ItamMailLayout.Sar(l.Amount)} is required before the responsibility is cleared." : $"The decision on {l.No}: not liable; the responsibility is cleared.", details, path, "Open the loss case", actor);
                if (l.Resp == "Cleared") await Notify(c, tx, "IT-EM-45", $"IT-EM-45|{l.No}", l.No, L(custodian, await LineManager(c, tx, l.Cu)), L(officers, managers), $"Your responsibility for {l.Asset} is cleared by the decision on {l.No}.", details, path, "Open the loss case", actor);
                break;
            case "settled": await Notify(c, tx, "IT-EM-45", $"IT-EM-45|{l.No}", l.No, L(custodian, await LineManager(c, tx, l.Cu)), L(officers, managers), $"The settlement is recorded: your responsibility for {l.Asset} is cleared ({l.No}).", details, path, "Open the loss case", actor); break;
            case "recovered": await Notify(c, tx, "IT-EM-46", $"IT-EM-46|{l.No}", l.No, L(investigator, await RoleHolders(c, tx, l.Region, ItamRoles.SupportSpecialist), officers, managers), custodian, $"{l.Asset} was recovered and received by IT ({l.No}); it is reviewed before reuse.", details, path, "Open the loss case", actor); break;
        }
    }

    internal async Task MailClearance(DbConnection c, DbTransaction tx, int id, string what, Guid actor)
    {
        var k = (await Rows(c, tx, "SELECT k.clearance_no,k.employee_id,e.display_name,e.account,e.department,e.branch,k.last_working_day FROM itam_data.clearance k JOIN itam_access.employee e ON e.employee_id=k.employee_id WHERE k.clearance_id=@i",
            r => (No: r.GetString(0), Emp: r.GetGuid(1), Name: r.GetString(2), Account: r.GetString(3), Dept: S(r, 4), Branch: S(r, 5), Last: Day(r, 6)), default, ("@i", id))).Single();
        var assets = await Rows(c, tx, "SELECT a.asset_no,t.name_en,COALESCE(a.serial_no,a.service_tag),d.computer_name,d.delivery_no,g.region_code FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code " +
            "JOIN itam_ref.location g ON g.location_id=a.location_id WHERE d.employee_id=@e AND d.received_at_utc IS NULL", r => (No: r.GetString(0), Type: r.GetString(1), Serial: S(r, 2), Computer: S(r, 3), Note: r.GetString(4), Region: r.GetString(5)), default, ("@e", k.Emp));
        (string, string?)[] details = [("Clearance", k.No), ("Employee", $"{k.Name} ({k.Account})"), ("Department", k.Dept), ("Branch", k.Branch), ("Last working day", k.Last is null ? null : ItamMailLayout.Date(k.Last)), ("Assets to collect", assets.Count.ToString())];
        var list = string.Join("\n", assets.Select(a => $"{a.No} · {a.Type}{(a.Serial is null ? "" : " · " + a.Serial)}{(a.Computer is null ? "" : " · " + a.Computer)} · {a.Note}"));
        switch (what)
        {
            case "resigned" when assets.Count > 0:
                var regional = new List<Rcpt>(); foreach (var g in assets.Select(a => a.Region).Distinct()) regional.AddRange(await RoleHolders(c, tx, g, ItamRoles.SupportSpecialist));
                await Notify(c, tx, "IT-EM-11", $"IT-EM-11|{k.No}", k.No, regional.Count > 0 ? regional : await RoleHolders(c, tx, null, ItamRoles.TeamLeader), L(await LineManager(c, tx, k.Emp), await RoleHolders(c, tx, null, ItamRoles.AssetsOfficer)),
                    $"{k.Name} is leaving: collect every asset they hold and record the receipt (or a sale).", details, "/leavers.html", "Open Assets to collect", actor, [("Assets to collect", list)]); break;
            case "completed": await Notify(c, tx, "IT-EM-64", $"IT-EM-64|{k.No}", k.No, await RoleHolders(c, tx, null, ItamRoles.SystemAdministrator), await RoleHolders(c, tx, null, ItamRoles.ItManager),
                $"The clearance of {k.Name} is completed: disable the user {k.Account} in Active Directory and keep it (do not delete it), then confirm it in IT Assets.", details, "/leavers.html", "Open Leavers", actor); break;
        }
    }

    internal async Task MailBackup(DbConnection c, DbTransaction? tx, int backupId)
    {
        var b = (await Rows(c, tx, "SELECT b.person_id,p.display_name,b.backup_employee_id,b.leave_from,b.leave_to,b.note FROM itam_access.leave_backup b JOIN itam_access.employee p ON p.employee_id=b.person_id WHERE b.backup_id=@i AND b.cancelled_at_utc IS NULL",
            r => (Person: r.GetGuid(0), Name: r.GetString(1), Backup: r.GetGuid(2), From: Day(r, 3), To: Day(r, 4), Note: S(r, 5)), default, ("@i", backupId))).SingleOrDefault();
        if (b.Name is null) return;
        await Notify(c, tx, "IT-EM-25", $"IT-EM-25|{backupId}", b.Name, await People(c, tx, b.Backup), L(await People(c, tx, b.Person), await LineManager(c, tx, b.Person)),
            $"You are the backup of {b.Name} from {ItamMailLayout.Date(b.From)} to {ItamMailLayout.Date(b.To)}: their approvals and tasks in IT Assets come to you during that time.",
            [("Backup for", b.Name), ("From", ItamMailLayout.Date(b.From)), ("To", ItamMailLayout.Date(b.To)), ("Note", b.Note)], "/alerts.html", "Open your alerts", null);
    }

    // ---- Reminders and weekly emails (the mail sender runs them every hour; each is sent once per period) ------------------------------
    public async Task<int> RunMailJobs(DateTime? nowUtc = null, CancellationToken token = default)
    {
        var now = (nowUtc ?? UtcNow).AddHours(3); var today = DateOnly.FromDateTime(now); var day = today.DayNumber;
        await using var c = await Open(token);
        var before = Convert.ToInt32(await Scalar(c, null, "SELECT COUNT(*) FROM itam_data.mail_outbox", token));
        // Backups starting today (IT-EM-25).
        foreach (var id in await Rows(c, null, "SELECT backup_id FROM itam_access.leave_backup WHERE cancelled_at_utc IS NULL AND leave_from<=@t AND leave_to>=@t", r => r.GetInt32(0), token, ("@t", DateParam(today))))
            await MailBackup(c, null, id);
        if (now.Hour >= 8)
        {
            // Signed copy waiting 3 days or more: the issuer, every 3 days (IT-EM-04); 7 days or more: the IT Manager weekly (IT-EM-05).
            var unsigned = await Rows(c, null, "SELECT d.delivery_no,a.asset_no,e.display_name,d.delivered_on,d.issued_by FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id JOIN itam_access.employee e ON e.employee_id=d.employee_id " +
                "WHERE d.received_at_utc IS NULL AND d.signed_file_id IS NULL AND d.delivered_on<=@t", r => (No: r.GetString(0), Asset: r.GetString(1), Emp: r.GetString(2), On: Day(r, 3)!.Value, By: r.GetGuid(4)), token, ("@t", DateParam(today.AddDays(-3))));
            foreach (var g in unsigned.GroupBy(u => u.By))
                await Notify(c, null, "IT-EM-04", $"IT-EM-04|{g.Key}|{day / 3}", $"{g.Count()} delivery note(s)", await People(c, null, g.Key), await RoleHolders(c, null, null, ItamRoles.TeamLeader),
                    "These delivery notes still wait for the copy the employee signed on paper: have them signed and upload the scans.", [("Delivery notes", g.Count().ToString())], "/handover.html", "Open Handover",
                    null, [("Waiting for the signed copy", string.Join("\n", g.Select(u => $"{u.No} · {u.Asset} · {u.Emp} · delivered {ItamMailLayout.Date(u.On)}")))]);
            var late = unsigned.Where(u => u.On <= today.AddDays(-7)).ToList();
            if (late.Count > 0) await Notify(c, null, "IT-EM-05", $"IT-EM-05|{day / 7}", $"{late.Count} delivery note(s)", await RoleHolders(c, null, null, ItamRoles.ItManager), await People(c, null, [.. late.Select(u => (Guid?)u.By)]),
                "These delivery notes are overdue for the signed copy (7 days or more).", [("Overdue", late.Count.ToString())], "/handover.html", "Open Handover", null,
                [("Overdue delivery notes", string.Join("\n", late.Select(u => $"{u.No} · {u.Asset} · {u.Emp} · delivered {ItamMailLayout.Date(u.On)}")))]);
            // Returnable exit passes overdue (IT-EM-21), transfers with assets not received by the expected date (IT-EM-37).
            foreach (var id in await Rows(c, null, "SELECT movement_id FROM itam_data.movement WHERE kind=N'Exit' AND status=N'Out' AND pass_type=N'Returnable' AND expected_date<@t", r => r.GetInt32(0), token, ("@t", DateParam(today))))
                await MailOverdueMovement(c, id, "IT-EM-21", day / 3);
            foreach (var id in await Rows(c, null, "SELECT DISTINCT m.movement_id FROM itam_data.movement m JOIN itam_data.movement_line l ON l.movement_id=m.movement_id WHERE l.state=N'Dispatched' AND m.expected_date<@t", r => r.GetInt32(0), token, ("@t", DateParam(today))))
                await MailOverdueMovement(c, id, "IT-EM-37", day / 3);
            // Approved disposals not completed in 14 days (IT-EM-49); maintenance overdue at the supplier (IT-EM-52); loss cases without action for 7 days (IT-EM-63).
            foreach (var (no, asset) in await Rows(c, null, "SELECT x.disposal_no,a.asset_no FROM itam_data.disposal x JOIN itam_data.asset a ON a.asset_id=x.asset_id WHERE x.status=N'Approved' AND x.decided_at_utc<@d", r => (r.GetString(0), r.GetString(1)), token, ("@d", UtcNow.AddDays(-14))))
                await Notify(c, null, "IT-EM-49", $"IT-EM-49|{no}|{day / 7}", no, await RoleHolders(c, null, null, ItamRoles.AssetsOfficer), await RoleHolders(c, null, null, ItamRoles.ItManager),
                    $"{no} was approved more than 14 days ago and is not completed: record the disposal of {asset} with its evidence.", [("Disposal", no), ("Asset No.", asset)], "/disposals.html", "Open the disposals", null);
            foreach (var (no, asset, by, region, expected) in await Rows(c, null, "SELECT m.order_no,a.asset_no,m.created_by,g.region_code,m.expected_return FROM itam_data.maintenance_order m JOIN itam_data.asset a ON a.asset_id=m.asset_id JOIN itam_ref.location g ON g.location_id=a.location_id WHERE m.status=N'WithSupplier' AND m.expected_return<@t",
                r => (r.GetString(0), r.GetString(1), r.GetGuid(2), r.GetString(3), Day(r, 4)), token, ("@t", DateParam(today))))
                await Notify(c, null, "IT-EM-52", $"IT-EM-52|{no}|{day / 3}", no, L(await RoleHolders(c, null, null, ItamRoles.AssetsOfficer), await People(c, null, by)), L(await RoleHolders(c, null, region, ItamRoles.SupportSpecialist), await RoleHolders(c, null, null, ItamRoles.ItManager)),
                    $"{asset} was expected back from the supplier on {ItamMailLayout.Date(expected)} and is still outside ({no}).", [("Order", no), ("Asset No.", asset), ("Expected return", ItamMailLayout.Date(expected))], "/maintenance.html", "Open the maintenance orders", null);
            foreach (var (id, no, status, iv) in await Rows(c, null, $"SELECT case_id,case_no,status,investigator_id FROM itam_data.loss_case WHERE status IN({OpenLossStatuses}) AND COALESCE(findings_at_utc,assigned_at_utc,reported_at_utc)<@d",
                r => (r.GetInt32(0), r.GetString(1), r.GetString(2), G0(r, 3)), token, ("@d", UtcNow.AddDays(-7))))
                await Notify(c, null, "IT-EM-63", $"IT-EM-63|{no}|{day / 7}", no, status == "UnderInvestigation" ? await People(c, null, iv) : await RoleHolders(c, null, null, ItamRoles.ItManager), await RoleHolders(c, null, null, ItamRoles.ItManager),
                    $"{no} has waited more than 7 days at \"{LossStatusLabel(status)}\".", [("Loss case", no), ("Status", LossStatusLabel(status))], "/loss.html?id=" + id, "Open the loss case", null);
            // Leavers: collection reminder every 3 days while assets are held (IT-EM-11).
            foreach (var (id, marked) in await Rows(c, null, "SELECT clearance_id,marked_at_utc FROM itam_data.clearance WHERE status=N'InProgress'", r => (r.GetInt32(0), Utc(r, 1)), token))
                if (marked < UtcNow.AddDays(-3)) { await using var tx = await c.BeginTransactionAsync(token); await MailClearanceReminder(c, tx, id, day / 3); await tx.CommitAsync(token); }
            // Pending decisions: one daily reminder to each person who has decisions waiting (IT-EM-61).
            foreach (var who in await Rows(c, null, "SELECT DISTINCT employee_id FROM itam_access.role_assignment UNION SELECT DISTINCT manager_id FROM itam_data.request WHERE status IN(N'WaitingRequestApproval',N'WaitingCostApproval') AND manager_id IS NOT NULL " +
                "UNION SELECT DISTINCT manager_id FROM itam_data.ink_request WHERE status=N'WaitingApproval' AND manager_id IS NOT NULL", r => r.GetGuid(0), token))
            {
                var s = await Scope(c, null, new ItamUser(who, ""), token);
                var decisions = (await AlertItems(c, s, token)).Where(a => a.Group == "Decisions").ToList();
                if (decisions.Count == 0) continue;
                await Notify(c, null, "IT-EM-61", $"IT-EM-61|{who}|{day}", $"{decisions.Sum(d => d.Count)} decision(s)", await People(c, null, who), [],
                    "These decisions are waiting for you in IT Assets.", [.. decisions.Select(d => (d.Label, (string?)d.Count.ToString()))], "/alerts.html", "Open your alerts", null);
            }
        }
        // Weekly, on Sunday from 07:00: the summary to the IT Manager (IT-EM-12) and the AD comparison report to the System Administrator (IT-EM-24).
        if (today.DayOfWeek == DayOfWeek.Sunday && now.Hour >= 7)
        {
            var week = $"{today.Year}-{System.Globalization.ISOWeek.GetWeekOfYear(now)}";
            foreach (var mgr in await RoleHolders(c, null, null, ItamRoles.ItManager))
            {
                var s = await Scope(c, null, new ItamUser(mgr.Id, ""), token);
                var items = await AlertItems(c, s, token);
                var counts = await Rows(c, null, "SELECT status,COUNT(*) FROM itam_data.asset GROUP BY status", r => (ItamStatuses.Label(r.GetString(0)), (string?)r.GetInt32(1).ToString()), token);
                await Notify(c, null, "IT-EM-12", $"IT-EM-12|{mgr.Id}|{week}", "Week " + week, [mgr], [], "The weekly summary of IT Assets: assets by status and the work waiting.", [.. counts],
                    "/dashboard.html", "Open the dashboard", null, [("Waiting", items.Count == 0 ? "Nothing is waiting." : string.Join("\n", items.Select(i => $"{i.Count} · {i.Label}")))]);
            }
            var run = (await Rows(c, null, "SELECT TOP 1 run_id,run_at_utc,computers,matched,no_delivery,removal_waiting,not_in_ad,needs_check,reviewed FROM itam_data.ad_run ORDER BY run_id DESC",
                r => (Id: r.GetInt32(0), At: Utc(r, 1), Computers: r.GetInt32(2), Matched: r.GetInt32(3), NoDelivery: r.GetInt32(4), Waiting: r.GetInt32(5), NotInAd: r.GetInt32(6), Check: r.GetInt32(7), Reviewed: r.GetInt32(8)), token)).FirstOrDefault();
            if (run.Id > 0)
                await Notify(c, null, "IT-EM-24", $"IT-EM-24|{week}", "Week " + week, await RoleHolders(c, null, null, ItamRoles.SystemAdministrator), [],
                    $"The latest AD computer comparison ({ItamMailLayout.When(run.At)}): the differences to follow up.",
                    [("Computers compared", run.Computers.ToString()), ("Matched", run.Matched.ToString()), ("In AD, no delivery note", run.NoDelivery.ToString()), ("Removal waiting", run.Waiting.ToString()),
                     ("On a delivery note, not in AD", run.NotInAd.ToString()), ("Matched, needs a check", run.Check.ToString()), ("Reviewed", run.Reviewed.ToString())], "/adcompare.html", "Open the AD comparison", null);
        }
        return Convert.ToInt32(await Scalar(c, null, "SELECT COUNT(*) FROM itam_data.mail_outbox", token)) - before;
    }

    async Task MailOverdueMovement(DbConnection c, int id, string code, int period)
    {
        var m = (await Rows(c, null, "SELECT movement_no,employee_id,requested_by,sender_id,receiver_id,expected_date FROM itam_data.movement WHERE movement_id=@i",
            r => (No: r.GetString(0), Emp: G0(r, 1), Req: r.GetGuid(2), Sender: G0(r, 3), Receiver: G0(r, 4), Expected: Day(r, 5)), default, ("@i", id))).Single();
        var to = code == "IT-EM-21" ? await People(c, null, m.Emp ?? m.Req) : await People(c, null, m.Receiver, m.Sender);
        var cc = code == "IT-EM-21" ? L(await LineManager(c, null, m.Emp ?? m.Req), await RoleHolders(c, null, null, ItamRoles.TeamLeader)) : await RoleHolders(c, null, null, ItamRoles.AssetsOfficer, ItamRoles.TeamLeader);
        await Notify(c, null, code, $"{code}|{m.No}|{period}", m.No, to, cc,
            code == "IT-EM-21" ? $"The assets on exit pass {m.No} were expected back on {ItamMailLayout.Date(m.Expected)}: bring them back and have IT confirm the return." : $"Assets on {m.No} were expected by {ItamMailLayout.Date(m.Expected)} and are not received yet: confirm the receipt or report what is missing.",
            [("Movement", m.No), ("Expected", ItamMailLayout.Date(m.Expected))], "/movement.html?id=" + id, "Open the movement", null);
    }

    async Task MailClearanceReminder(DbConnection c, DbTransaction tx, int id, int period)
    {
        var no = (string)(await Scalar(c, tx, "SELECT clearance_no FROM itam_data.clearance WHERE clearance_id=@i", default, ("@i", id)))!;
        var open = Convert.ToInt32(await Scalar(c, tx, "SELECT COUNT(*) FROM itam_data.delivery d JOIN itam_data.clearance k ON k.employee_id=d.employee_id WHERE k.clearance_id=@i AND d.received_at_utc IS NULL", default, ("@i", id)));
        if (open == 0) return;
        // The reminder is IT-EM-11 again, keyed by period, so it is sent every 3 days while assets are held.
        await MailClearanceAs(c, tx, id, $"IT-EM-11|{no}|{period}");
    }
    async Task MailClearanceAs(DbConnection c, DbTransaction tx, int id, string key)
    {
        // Same purpose as the first collection email, with the reminder key.
        var k = (await Rows(c, tx, "SELECT k.clearance_no,k.employee_id,e.display_name FROM itam_data.clearance k JOIN itam_access.employee e ON e.employee_id=k.employee_id WHERE k.clearance_id=@i", r => (No: r.GetString(0), Emp: r.GetGuid(1), Name: r.GetString(2)), default, ("@i", id))).Single();
        var regions = await Rows(c, tx, "SELECT DISTINCT g.region_code FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id JOIN itam_ref.location g ON g.location_id=a.location_id WHERE d.employee_id=@e AND d.received_at_utc IS NULL", r => r.GetString(0), default, ("@e", k.Emp));
        var regional = new List<Rcpt>(); foreach (var g in regions) regional.AddRange(await RoleHolders(c, tx, g, ItamRoles.SupportSpecialist));
        await Notify(c, tx, "IT-EM-11", key, k.No, regional, L(await LineManager(c, tx, k.Emp), await RoleHolders(c, tx, null, ItamRoles.AssetsOfficer)),
            $"Reminder: {k.Name} still holds company assets; collect them and record the receipts.", [("Clearance", k.No), ("Employee", k.Name)], "/leavers.html", "Open Assets to collect", null);
    }

    // ---- The outbox page (IT Manager) --------------------------------------------------------------------------------------------------
    public async Task<object> MailOutbox(ItamUser user, string? status, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.Manager, "Only the IT Manager sees the email outbox.");
        var rows = await Rows(c, null, "SELECT TOP 500 mail_id,template,record_ref,to_list,cc_list,recipients,subject,status,attempts,last_error,created_at_utc,sent_at_utc FROM itam_data.mail_outbox WHERE (@s IS NULL OR status=@s) ORDER BY mail_id DESC",
            r => new { id = r.GetInt64(0), template = r.GetString(1), name = MailNames.GetValueOrDefault(r.GetString(1)), record = S(r, 2), to = r.GetString(3), cc = S(r, 4), recipients = S(r, 5), subject = r.GetString(6), status = r.GetString(7),
                attempts = r.GetInt32(8), error = S(r, 9), createdAt = Utc(r, 10), sentAt = r.IsDBNull(11) ? (DateTime?)null : Utc(r, 11) }, token, ("@s", string.IsNullOrEmpty(status) ? null : status));
        var counts = await Rows(c, null, "SELECT status,COUNT(*) FROM itam_data.mail_outbox GROUP BY status", r => new { status = r.GetString(0), count = r.GetInt32(1) }, token);
        return new { mails = rows, counts };
    }

    public async Task<string> MailHtml(ItamUser user, long id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.Manager, "Only the IT Manager sees the email outbox.");
        var html = await Scalar(c, null, "SELECT body_html FROM itam_data.mail_outbox WHERE mail_id=@i", token, ("@i", id)) as string ?? throw new ArgumentException("The email was not found.");
        return html.Replace("cid:logo", SiteUrl + "/company-logo.png");
    }

    public async Task RetryMail(ItamUser user, long id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.Manager, "Only the IT Manager retries emails.");
        await Exec(c, null, "UPDATE itam_data.mail_outbox SET status=N'Queued',attempts=0,last_error=NULL WHERE mail_id=@i AND status=N'Failed'", token, ("@i", id));
    }

    public async Task<int> RunMailJobsNow(ItamUser user, CancellationToken token)
    {
        await using (var c = await Open(token)) Require((await Scope(c, null, user, token)).Manager, "Only the IT Manager runs the reminders.");
        return await RunMailJobs(null, token);
    }

    /// <summary>Sends the queued emails through the mail service (after the actions committed). Retries 5 times, then Failed
    /// (shown on the outbox page; a failure is never emailed). A test recipient, when set, receives every email instead.</summary>
    public async Task<int> SendQueuedMail(IItamMailer mailer, string? testRecipient, byte[] logo, CancellationToken token)
    {
        await using var c = await Open(token);
        var queued = await Rows(c, null, "SELECT TOP 20 mail_id,to_list,cc_list,subject,body_html FROM itam_data.mail_outbox WHERE status=N'Queued' ORDER BY mail_id",
            r => (Id: r.GetInt64(0), To: r.GetString(1), Cc: S(r, 2), Subject: r.GetString(3), Html: r.GetString(4)), token);
        var sent = 0;
        foreach (var m in queued)
        {
            var to = m.To.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            var cc = (m.Cc ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            var html = m.Html;
            if (!string.IsNullOrWhiteSpace(testRecipient))
            {
                html = ItamMailTest.Banner(html, $"TEST: this email was for {m.To}{(cc.Count == 0 ? "" : " (CC " + m.Cc + ")")}. It was redirected to the test mailbox.");
                to = [testRecipient.Trim()]; cc = [];
            }
            try
            {
                await mailer.Send(new ItamMailMessage(to, cc, m.Subject, html, [new ItamMailImage("logo", "logo.png", "image/png", logo)]), token);
                await Exec(c, null, "UPDATE itam_data.mail_outbox SET status=N'Sent',sent_at_utc=SYSUTCDATETIME(),sent_to=@t,attempts=attempts+1,last_error=NULL WHERE mail_id=@i", token,
                    ("@t", string.Join("; ", to.Concat(cc))), ("@i", m.Id));
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Exec(c, null, "UPDATE itam_data.mail_outbox SET attempts=attempts+1,last_error=@e,status=CASE WHEN attempts+1>=5 THEN N'Failed' ELSE N'Queued' END WHERE mail_id=@i", token,
                    ("@e", ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message), ("@i", m.Id));
            }
        }
        return sent;
    }
}

static class ItamMailTest
{
    const string Marker = "<td align=\"center\" style=\"padding:20px 8px\">";
    public static string Banner(string html, string notice)
    {
        var i = html.IndexOf(Marker, StringComparison.Ordinal);
        if (i < 0) return html;
        var banner = "<table role=\"presentation\" width=\"720\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:720px;max-width:100%;margin-bottom:8px\"><tr><td bgcolor=\"#fff4e5\" style=\"background:#fff4e5;border:1px solid #f0b429;color:#7a4b00;padding:6px 10px;font-size:12px;font-family:Calibri,Arial,sans-serif\">" +
            System.Net.WebUtility.HtmlEncode(notice) + "</td></tr></table>";
        return html.Insert(i + Marker.Length, banner);
    }
}
