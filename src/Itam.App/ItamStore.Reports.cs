using System.Data.Common;

namespace Itam.App;

/// <summary>Dashboard and reports (plan v1.9: assets by status, type, region and branch; assets per employee; leavers holding
/// assets; ageing; maintenance cost; ink use; and the end-of-life, movement and purchase reports). Every report keeps the
/// person's visibility: assets by type and region as on the Assets page, costs only for the roles that see purchase orders,
/// and the module reports for the roles of that module. Each report opens on screen and exports to Excel.</summary>
public sealed partial class ItamStore
{
    public sealed record ItamReportDef(string Key, string Title, string Description, string Area, bool Dates);
    public sealed record ItamReportData(string Key, string Title, string[] Columns, List<object?[]> Rows, string[] Regions);

    static readonly ItamReportDef[] ReportDefs =
    [
        new("assets", "Asset register", "Every asset with its status, where it is kept, its holder and its purchase details.", "Assets", true),
        new("by-type", "Assets by type and status", "How many assets of each type are in each status.", "Assets", false),
        new("by-location", "Assets by region and location", "How many assets are kept in each region, warehouse and branch, by status.", "Assets", false),
        new("employees", "Assets per employee", "Who holds what: each employee with the assets in their custody.", "Assets", false),
        new("ageing", "Asset ageing", "Age of each asset from its purchase date, in bands.", "Assets", false),
        new("available-computers", "Available computers by computer type", "Computers In Store, by computer type and where they are kept; reserved ones shown apart.", "Assets", false),
        new("available-computers-list", "Available computers (list)", "Each computer In Store with its type, processor, memory, storage and operating system.", "Assets", false),
        new("unsigned", "Delivery notes not signed", "Delivery notes still waiting for the copy the employee signed on paper.", "Handover", true),
        new("leavers", "Leavers holding assets", "Employees marked Resigned with assets still to collect.", "End of life", false),
        new("maintenance", "Maintenance cost", "Maintenance orders with the quotation and the final cost.", "Maintenance", true),
        new("ink", "Ink use", "Ink requests with the toner, quantity and delivery.", "Ink", true),
        new("ink-printers", "Ink requests by printer", "Each printer with its ink requests, toners delivered and the last request.", "Ink", true),
        new("pos", "Purchase orders received", "Each PO line: ordered, received and the cost.", "Purchases", true),
        new("movements", "Gate passes and transfers", "Branch transfers, exit passes and warehouse transfers.", "Movements", true),
        new("sales", "Sales", "Devices sold to employees, with the price.", "End of life", true),
        new("disposals", "Disposals", "Disposal requests, decisions and completions.", "End of life", true),
        new("loss", "Loss cases", "Lost or stolen assets with the decision and the write-off.", "End of life", true)
    ];

    static bool CanReport(ItamScope s, string key) => key switch
    {
        "maintenance" => s.MaintenanceView, "ink" or "ink-printers" => s.InkView || s.InvoiceToners, "pos" => s.PoView, "movements" => s.CanHandoverAny || s.ViewsAll,
        "sales" => s.SalesView && s.CanViewAny, "disposals" => s.DisposalView, "loss" => s.LossViewAny, "leavers" => s.CollectView || s.MarkResigned,
        "unsigned" => s.CanHandoverAny, _ => s.CanViewAny
    };

    public async Task<object> Reports(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.CanViewAny, "Reports are for IT.");
        return new { reports = ReportDefs.Where(r => CanReport(s, r.Key)).Select(r => new { key = r.Key, title = r.Title, description = r.Description, area = r.Area, dates = r.Dates }),
            regions = s.ViewsAll ? await Rows(c, null, "SELECT region_code FROM itam_ref.region WHERE active=1 ORDER BY region_code", r => r.GetString(0), token) : s.Regions.ToList() };
    }

    static readonly string[] StatusOrder = ["InStore", "InUse", "InTransit", "InRepair", "PendingDisposal", "Lost", "Disposed", "Sold", "Expired"];
    static string AgeBand(DateOnly? bought, DateOnly today) => bought is not DateOnly b ? "Unknown" : ((today.DayNumber - b.DayNumber) / 365.25) switch
    { < 1 => "Under 1 year", < 3 => "1 to 3 years", < 5 => "3 to 5 years", _ => "5 years and more" };
    static readonly string[] AgeBands = ["Under 1 year", "1 to 3 years", "3 to 5 years", "5 years and more", "Unknown"];

    sealed record AssetFact(int Id, string No, string TypeCode, string Type, string? Maker, string? Model, string? Serial, string? Tag, string Status, string KeptAt, string Kind, string Region,
        string? Holder, string? Username, Guid? HolderId, string? Dept, string? Branch, string? DeliveryNo, DateOnly? DeliveredOn, bool Signed, DateOnly? Purchase, string? Supplier, string? Po, decimal? Cost, DateTime Created);
    static async Task<List<AssetFact>> AssetFacts(DbConnection c, ItamScope s, CancellationToken token)
        => (await Rows(c, null, "SELECT a.asset_id,a.asset_no,a.type_code,t.name_en,mf.name_en,mo.name_en,a.serial_no,a.service_tag,a.status,g.name_en,g.kind,g.region_code," +
            "e.display_name,e.account,e.employee_id,e.department,e.branch,d.delivery_no,d.delivered_on,CASE WHEN d.signed_file_id IS NULL THEN 0 ELSE 1 END,a.purchase_date,su.name_en,COALESCE(p.po_number,a.po_number_text),a.unit_cost,a.created_at_utc " +
            "FROM itam_data.asset a JOIN itam_ref.asset_type t ON t.type_code=a.type_code JOIN itam_ref.location g ON g.location_id=a.location_id " +
            "LEFT JOIN itam_ref.manufacturer mf ON mf.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id LEFT JOIN itam_ref.supplier su ON su.supplier_id=a.supplier_id " +
            "LEFT JOIN itam_data.po p ON p.po_id=a.po_id LEFT JOIN itam_data.delivery d ON d.asset_id=a.asset_id AND d.received_at_utc IS NULL LEFT JOIN itam_access.employee e ON e.employee_id=d.employee_id ORDER BY a.asset_seq",
            r => new AssetFact(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), S(r, 4), S(r, 5), S(r, 6), S(r, 7), r.GetString(8), r.GetString(9), r.GetString(10), r.GetString(11),
                S(r, 12), S(r, 13), G0(r, 14), S(r, 15), S(r, 16), S(r, 17), Day(r, 18), r.GetInt32(19) == 1, Day(r, 20), S(r, 21), S(r, 22), r.IsDBNull(23) ? null : r.GetDecimal(23), Utc(r, 24)), token))
            .Where(a => s.CanView(a.TypeCode, a.Region)).ToList();


    // Available computers by computer type (owner, 22 October 2026): computers In Store, minus those reserved for a device request.
    sealed record ComputerFact(int Id, string No, string Type, string? TypeCode, string? Model, string? Tag, string? Processor, string? Memory, string? Storage, string? Os, string KeptAt, string Region, bool Reserved);
    static async Task<List<ComputerFact>> InStoreComputers(DbConnection c, ItamScope s, CancellationToken token)
        => (await Rows(c, null, "SELECT a.asset_id,a.asset_no,COALESCE(vt.name_en,ct.value,N'Not set'),CONCAT(mf.name_en,' ',mo.name_en),a.service_tag," +
            "(SELECT value FROM itam_data.asset_attribute x WHERE x.asset_id=a.asset_id AND x.attr_code=N'Processor'),(SELECT value FROM itam_data.asset_attribute x WHERE x.asset_id=a.asset_id AND x.attr_code=N'MemoryGb')," +
            "(SELECT value FROM itam_data.asset_attribute x WHERE x.asset_id=a.asset_id AND x.attr_code=N'CapacityGb'),COALESCE(vo.name_en,os.value),g.name_en,g.region_code," +
            "CASE WHEN EXISTS(SELECT 1 FROM itam_data.request q WHERE q.reserved_asset_id=a.asset_id AND q.status=N'StockAssigned') THEN 1 ELSE 0 END,ct.value " +
            "FROM itam_data.asset a JOIN itam_ref.location g ON g.location_id=a.location_id LEFT JOIN itam_ref.manufacturer mf ON mf.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id " +
            "LEFT JOIN itam_data.asset_attribute ct ON ct.asset_id=a.asset_id AND ct.attr_code=N'ComputerType' LEFT JOIN itam_ref.value_item vt ON vt.list_code=N'ComputerType' AND vt.item_code=ct.value " +
            "LEFT JOIN itam_data.asset_attribute os ON os.asset_id=a.asset_id AND os.attr_code=N'OperatingSystem' LEFT JOIN itam_ref.value_item vo ON vo.list_code=N'OperatingSystem' AND vo.item_code=os.value " +
            "WHERE a.type_code=N'PC' AND a.status=N'InStore' ORDER BY a.asset_seq",
            r => new ComputerFact(r.GetInt32(0), r.GetString(1), r.GetString(2), S(r, 12), S(r, 3)?.Trim(), S(r, 4), S(r, 5), S(r, 6), S(r, 7), S(r, 8), r.GetString(9), r.GetString(10), r.GetInt32(11) == 1), token))
            .Where(x => s.CanView("PC", x.Region)).ToList();
    public async Task<ItamReportData> Report(ItamUser user, string key, DateOnly? from, DateOnly? to, string? region, CancellationToken token)
    {
        var def = ReportDefs.FirstOrDefault(r => r.Key == key) ?? throw new ArgumentException("Unknown report.");
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(CanReport(s, key), "You cannot open this report.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        bool InDates(DateOnly? d) => !def.Dates || d is null || ((from is null || d >= from) && (to is null || d <= to));
        bool InDatesT(DateTime? t) => InDates(t is DateTime x ? DateOnly.FromDateTime(x.AddHours(3)) : null);
        bool Reg(string? g) => (string.IsNullOrEmpty(region) || g == region) && (g is null ? s.ViewsAll : s.ViewsAll || s.CollectRegion(g) || s.InkRegions.Contains(g));
        string L(string status) => ItamStatuses.Label(status);
        List<object?[]> rows; string[] cols;
        switch (key)
        {
            case "assets":
            {
                var a = (await AssetFacts(c, s, token)).Where(x => InDatesT(x.Created) && (string.IsNullOrEmpty(region) || x.Region == region)).ToList();
                cols = ["Asset No.", "Type", "Manufacturer", "Model", "Serial No.", "Service tag", "Status", "Kept at", "Region", "Employee", "Username", "Delivery note", "Delivered on", "Purchase date", "Supplier", "PO", .. s.PoView ? new[] { "Unit cost (SAR)" } : [], "Registered"];
                rows = a.Select(x => (object?[])[x.No, x.Type, x.Maker, x.Model, x.Serial, x.Tag, L(x.Status), x.KeptAt, x.Region, x.Holder, x.Username, x.DeliveryNo, x.DeliveredOn, x.Purchase, x.Supplier, x.Po,
                    .. s.PoView ? new object?[] { x.Cost } : [], x.Created]).ToList();
                break;
            }
            case "by-type":
            {
                var a = (await AssetFacts(c, s, token)).Where(x => string.IsNullOrEmpty(region) || x.Region == region).ToList();
                cols = ["Type", .. StatusOrder.Select(L), "Total"];
                rows = a.GroupBy(x => x.Type).OrderBy(g => g.Key).Select(g => (object?[])[g.Key, .. StatusOrder.Select(st => (object?)g.Count(x => x.Status == st)), g.Count()]).ToList();
                rows.Add(["Total", .. StatusOrder.Select(st => (object?)a.Count(x => x.Status == st)), a.Count]);
                break;
            }
            case "by-location":
            {
                var a = (await AssetFacts(c, s, token)).Where(x => string.IsNullOrEmpty(region) || x.Region == region).ToList();
                cols = ["Region", "Kept at", "Kind", .. StatusOrder.Take(6).Select(L), "Total"];
                rows = a.GroupBy(x => (x.Region, x.KeptAt, x.Kind)).OrderBy(g => g.Key.Region).ThenBy(g => g.Key.KeptAt)
                    .Select(g => (object?[])[g.Key.Region, g.Key.KeptAt, g.Key.Kind switch { "ITWarehouse" => "IT warehouse", _ => g.Key.Kind }, .. StatusOrder.Take(6).Select(st => (object?)g.Count(x => x.Status == st)), g.Count()]).ToList();
                break;
            }
            case "employees":
            {
                var a = (await AssetFacts(c, s, token)).Where(x => x.HolderId is not null && (string.IsNullOrEmpty(region) || x.Region == region)).ToList();
                cols = ["Employee", "Username", "Department", "Branch", "Assets", "Asset Nos.", "Not signed"];
                rows = a.GroupBy(x => x.HolderId).Select(g => g.First()).OrderBy(x => x.Holder).Select(f =>
                {
                    var mine = a.Where(x => x.HolderId == f.HolderId).ToList();
                    return (object?[])[f.Holder, f.Username, f.Dept, f.Branch, mine.Count, string.Join(", ", mine.Select(x => x.No)), mine.Count(x => !x.Signed)];
                }).ToList();
                break;
            }
            case "ageing":
            {
                var a = (await AssetFacts(c, s, token)).Where(x => x.Status is not ("Disposed" or "Sold") && (string.IsNullOrEmpty(region) || x.Region == region)).ToList();
                cols = ["Asset No.", "Type", "Model", "Purchase date", "Age (years)", "Age band", "Status", "Kept at", "Region", "Employee"];
                rows = a.OrderBy(x => x.Purchase ?? DateOnly.MaxValue).Select(x => (object?[])[x.No, x.Type, string.Join(" ", new[] { x.Maker, x.Model }.Where(v => v is not null)), x.Purchase,
                    x.Purchase is DateOnly b ? Math.Round((today.DayNumber - b.DayNumber) / 365.25m, 1) : null, AgeBand(x.Purchase, today), L(x.Status), x.KeptAt, x.Region, x.Holder]).ToList();
                break;
            }
            case "available-computers":
            {
                var a = (await InStoreComputers(c, s, token)).Where(x => string.IsNullOrEmpty(region) || x.Region == region).ToList();
                cols = ["Computer type", "Kept at", "Region", "Available", "Reserved for a request", "In store"];
                rows = a.GroupBy(x => (x.Type, x.KeptAt, x.Region)).OrderBy(g => g.Key.Type).ThenBy(g => g.Key.Region).ThenBy(g => g.Key.KeptAt)
                    .Select(g => (object?[])[g.Key.Type, g.Key.KeptAt, g.Key.Region, g.Count(x => !x.Reserved), g.Count(x => x.Reserved), g.Count()]).ToList();
                foreach (var t in a.GroupBy(x => x.Type).OrderBy(g => g.Key))
                    rows.Add([$"Total {t.Key}", null, null, t.Count(x => !x.Reserved), t.Count(x => x.Reserved), t.Count()]);
                rows.Add(["Total", null, null, a.Count(x => !x.Reserved), a.Count(x => x.Reserved), a.Count]);
                break;
            }
            case "available-computers-list":
            {
                var a = (await InStoreComputers(c, s, token)).Where(x => string.IsNullOrEmpty(region) || x.Region == region).ToList();
                cols = ["Asset No.", "Computer type", "Model", "Service tag", "Processor", "Memory (GB)", "Storage (GB)", "Operating system", "Kept at", "Region", "Available"];
                rows = a.OrderBy(x => x.Type).ThenBy(x => x.Region).Select(x => (object?[])[x.No, x.Type, x.Model, x.Tag, x.Processor, x.Memory, x.Storage, x.Os, x.KeptAt, x.Region,
                    x.Reserved ? "Reserved for a request" : "Available"]).ToList();
                break;
            }
            case "unsigned":
            {
                var a = (await AssetFacts(c, s, token)).Where(x => x.DeliveryNo is not null && !x.Signed && InDates(x.DeliveredOn) && (string.IsNullOrEmpty(region) || x.Region == region) && s.CanHandover(x.Region)).ToList();
                cols = ["Delivery note", "Asset No.", "Type", "Employee", "Username", "Delivered on", "Days waiting", "Region"];
                rows = a.OrderBy(x => x.DeliveredOn).Select(x => (object?[])[x.DeliveryNo, x.No, x.Type, x.Holder, x.Username, x.DeliveredOn, x.DeliveredOn is DateOnly d ? today.DayNumber - d.DayNumber : null, x.Region]).ToList();
                break;
            }
            case "leavers":
            {
                var raw = await Rows(c, null, "SELECT e.display_name,e.account,k.clearance_no,k.last_working_day,a.asset_no,t.name_en,COALESCE(a.serial_no,a.service_tag),d.delivery_no,g.region_code,k.marked_at_utc,a.status " +
                    "FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code JOIN itam_ref.location g ON g.location_id=a.location_id " +
                    "JOIN itam_access.employee e ON e.employee_id=d.employee_id LEFT JOIN itam_data.clearance k ON k.employee_id=d.employee_id AND k.status=N'InProgress' WHERE d.received_at_utc IS NULL AND d.owner_type=N'Resigned' ORDER BY e.display_name,a.asset_no",
                    r => (Region: r.GetString(8), Row: (object?[])[r.GetString(0), r.GetString(1), S(r, 2), Day(r, 3), r.GetString(4), r.GetString(5), S(r, 6), r.GetString(7), r.GetString(8),
                        r.IsDBNull(9) ? null : (int)(UtcNow - Utc(r, 9)).TotalDays, r.GetString(10) == "Lost" ? "Lost (loss case)" : "To collect"]), token);
                cols = ["Employee", "Username", "Clearance", "Last working day", "Asset No.", "Type", "Serial / service tag", "Delivery note", "Region", "Days pending", "State"];
                rows = raw.Where(x => Reg(x.Region) || s.MarkResigned).Select(x => x.Row).ToList();
                break;
            }
            case "maintenance":
            {
                var raw = await Rows(c, null, "SELECT m.order_no,a.asset_no,t.name_en,su.name_en,m.created_at_utc,m.status,m.dispatched_on,m.returned_on,m.outcome,m.cost,m.final_cost,m.invoice_no,g.region_code " +
                    "FROM itam_data.maintenance_order m JOIN itam_data.asset a ON a.asset_id=m.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code JOIN itam_ref.supplier su ON su.supplier_id=m.supplier_id " +
                    "JOIN itam_ref.location g ON g.location_id=a.location_id ORDER BY m.order_id",
                    r => (At: Utc(r, 4), Region: r.GetString(12), Row: (object?[])[r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Utc(r, 4), OrderStatusLabel(r.GetString(5)), Day(r, 6), Day(r, 7), S(r, 8),
                        r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetDecimal(10), S(r, 11), r.GetString(12)]), token);
                cols = ["Order", "Asset No.", "Type", "Supplier", "Created", "Status", "Sent", "Back", "Outcome", "Quotation cost (SAR)", "Final cost (SAR)", "Invoice", "Region"];
                rows = raw.Where(x => InDatesT(x.At) && (string.IsNullOrEmpty(region) || x.Region == region)).Select(x => x.Row).ToList();
                rows.Add(["Total", null, null, null, null, null, null, null, null, rows.Sum(r => (decimal)r[9]!), rows.Sum(r => r[10] as decimal? ?? 0), null, null]);
                break;
            }
            case "ink":
            {
                var raw = await Rows(c, null, "SELECT q.request_no,q.requested_at_utc,e.display_name,e.department,q.region_code,COALESCE(pa.asset_no,N'S/N '+q.new_serial),q.toner,q.quantity,q.status,dv.delivered_on,dv.unit_cost " +
                    "FROM itam_data.ink_request q JOIN itam_access.employee e ON e.employee_id=q.employee_id LEFT JOIN itam_data.asset pa ON pa.asset_id=q.printer_asset_id LEFT JOIN itam_data.ink_delivery dv ON dv.request_id=q.request_id ORDER BY q.request_id",
                    r => (At: Utc(r, 1), Region: S(r, 4), Row: (object?[])[r.GetString(0), Utc(r, 1), r.GetString(2), S(r, 3), S(r, 4), S(r, 5), r.GetString(6), r.GetInt32(7), r.GetString(8), Day(r, 9),
                        r.IsDBNull(10) ? null : r.GetDecimal(10)]), token);
                cols = ["Request", "Requested", "Employee", "Department", "Region", "Printer", "Toner", "Quantity", "Status", "Delivered on", .. s.InvoiceToners ? new[] { "Unit cost (SAR)" } : []];
                rows = raw.Where(x => InDatesT(x.At) && (string.IsNullOrEmpty(region) || x.Region == region) && (s.InkView || s.InvoiceToners || s.CanInk(x.Region)))
                    .Select(x => s.InvoiceToners ? x.Row : x.Row[..10]).ToList();
                break;
            }
            case "ink-printers":
            {
                // Ink requests by printer (owner, 23 October 2026): one row per printer (a printer not in IT Assets by its serial number).
                var raw = await Rows(c, null, "SELECT COALESCE(pa.asset_no,N'S/N '+q.new_serial),CASE WHEN pa.asset_id IS NULL THEN ISNULL(nm.name_en+N' '+nmo.name_en,q.new_model) ELSE CONCAT(mf.name_en,' ',mo.name_en) END," +
                    "COALESCE(pl.value,q.employee_location),g.name_en,q.region_code,q.requested_at_utc,q.toner,q.quantity,q.status,dv.delivered_on,dv.unit_cost,e.display_name,pa.serial_no " +
                    "FROM itam_data.ink_request q JOIN itam_access.employee e ON e.employee_id=q.employee_id LEFT JOIN itam_data.asset pa ON pa.asset_id=q.printer_asset_id " +
                    "LEFT JOIN itam_ref.manufacturer mf ON mf.manufacturer_id=pa.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=pa.model_id LEFT JOIN itam_ref.location g ON g.location_id=pa.location_id " +
                    "LEFT JOIN itam_data.asset_attribute pl ON pl.asset_id=pa.asset_id AND pl.attr_code=N'PrinterLocation' LEFT JOIN itam_ref.model nmo ON nmo.model_id=q.new_model_id LEFT JOIN itam_ref.manufacturer nm ON nm.manufacturer_id=nmo.manufacturer_id " +
                    "LEFT JOIN itam_data.ink_delivery dv ON dv.request_id=q.request_id ORDER BY q.request_id",
                    r => (Printer: r.GetString(0), Model: S(r, 1)?.Trim(), Where: S(r, 2), KeptAt: S(r, 3), Region: S(r, 4), At: Utc(r, 5), Toner: r.GetString(6), Qty: r.GetInt32(7), Status: r.GetString(8),
                        Delivered: Day(r, 9), Cost: r.IsDBNull(10) ? (decimal?)null : r.GetDecimal(10), Employee: r.GetString(11), Serial: S(r, 12)), token);
                var mine = raw.Where(x => InDatesT(x.At) && (string.IsNullOrEmpty(region) || x.Region == region) && (s.InkView || s.InvoiceToners || s.CanInk(x.Region))).ToList();
                cols = ["Printer", "Serial No.", "Model", "Printer location", "Kept at", "Region", "Requests", "Delivered", "Toners delivered", "Rejected or cancelled", "Toners used", "First request", "Last request", "Last delivered", "Last asked by",
                    .. s.InvoiceToners ? new[] { "Toner cost (SAR)" } : []];
                rows = mine.GroupBy(x => x.Printer).Select(g =>
                {
                    var last = g.MaxBy(x => x.At);
                    var delivered = g.Where(x => x.Status == "Delivered").ToList();
                    return (object?[])[g.Key, g.First().Serial, last.Model, last.Where, last.KeptAt, last.Region, g.Count(), delivered.Count, delivered.Sum(x => x.Qty), g.Count(x => x.Status is "Rejected" or "Cancelled"),
                        string.Join(", ", g.Select(x => x.Toner).Distinct()), g.Min(x => x.At), last.At, delivered.Max(x => x.Delivered), last.Employee,
                        .. s.InvoiceToners ? new object?[] { delivered.Sum(x => (x.Cost ?? 0) * x.Qty) } : []];
                }).OrderByDescending(r => (int)r[8]!).ThenBy(r => (string)r[0]!).ToList();
                if (rows.Count > 0)
                    rows.Add(["Total", null, null, null, null, null, rows.Sum(r => (int)r[6]!), rows.Sum(r => (int)r[7]!), rows.Sum(r => (int)r[8]!), rows.Sum(r => (int)r[9]!), null, null, null, null, null,
                        .. s.InvoiceToners ? new object?[] { rows.Sum(r => (decimal)r[15]!) } : []]);
                break;
            }
            case "pos":
            {
                var raw = await Rows(c, null, "SELECT p.po_number,p.po_date,su.name_en,l.line_no,t.name_en,CONCAT(mf.name_en,' ',mo.name_en),l.quantity,(SELECT COUNT(*) FROM itam_data.asset a WHERE a.po_line_id=l.po_line_id),l.unit_cost " +
                    "FROM itam_data.po p JOIN itam_data.po_line l ON l.po_id=p.po_id JOIN itam_ref.supplier su ON su.supplier_id=p.supplier_id JOIN itam_ref.asset_type t ON t.type_code=l.type_code " +
                    "LEFT JOIN itam_ref.manufacturer mf ON mf.manufacturer_id=l.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=l.model_id ORDER BY p.po_date,p.po_number,l.line_no",
                    r => (On: Day(r, 1), Row: (object?[])[r.GetString(0), Day(r, 1), r.GetString(2), r.GetInt32(3), r.GetString(4), S(r, 5)?.Trim(), r.GetInt32(6), r.GetInt32(7), r.GetInt32(6) - r.GetInt32(7), r.GetDecimal(8), r.GetInt32(6) * r.GetDecimal(8)]), token);
                cols = ["PO", "PO date", "Supplier", "Line", "Type", "Model", "Ordered", "Received", "Still to receive", "Unit cost (SAR)", "Line total (SAR)"];
                rows = raw.Where(x => InDates(x.On)).Select(x => x.Row).ToList();
                break;
            }
            case "movements":
            {
                var raw = await Rows(c, null, "SELECT m.movement_no,m.kind,m.purpose,fg.name_en,tg.name_en,m.destination,m.requested_at_utc,m.exit_date,m.valid_until,m.status,(SELECT COUNT(*) FROM itam_data.movement_line l WHERE l.movement_id=m.movement_id AND l.state<>N'Cancelled')," +
                    "se.display_name,re.display_name,fg.region_code,tg.region_code FROM itam_data.movement m JOIN itam_ref.location fg ON fg.location_id=m.from_location_id LEFT JOIN itam_ref.location tg ON tg.location_id=m.to_location_id " +
                    "LEFT JOIN itam_access.employee se ON se.employee_id=m.sender_id LEFT JOIN itam_access.employee re ON re.employee_id=m.receiver_id ORDER BY m.movement_id",
                    r => (At: Utc(r, 6), From: r.GetString(13), To: S(r, 14), Row: (object?[])[r.GetString(0), r.GetString(1) switch { "Branch" => "Branch transfer", "Exit" => "Exit pass", _ => "Warehouse transfer" }, r.GetString(2),
                        r.GetString(3), S(r, 4) ?? S(r, 5), Utc(r, 6), Day(r, 7), Day(r, 8), MovementStatusLabel(r.GetString(9)), r.GetInt32(10), S(r, 11), S(r, 12)]), token);
                cols = ["No.", "Kind", "Purpose", "From", "To", "Requested", "Exit date", "Valid until", "Status", "Assets", "Sender", "Receiver"];
                rows = raw.Where(x => InDatesT(x.At) && (s.ViewsAll || s.CollectRegion(x.From) || (x.To is string t && s.CollectRegion(t))) && (string.IsNullOrEmpty(region) || x.From == region || x.To == region)).Select(x => x.Row).ToList();
                break;
            }
            case "sales":
            {
                var raw = await Rows(c, null, "SELECT x.sale_no,a.asset_no,t.name_en,a.type_code,e.display_name,e.account,x.price,x.sold_on,rb.display_name,g.region_code FROM itam_data.sale x JOIN itam_data.asset a ON a.asset_id=x.asset_id " +
                    "JOIN itam_ref.asset_type t ON t.type_code=a.type_code JOIN itam_access.employee e ON e.employee_id=x.buyer_id JOIN itam_ref.location g ON g.location_id=x.location_id LEFT JOIN itam_access.employee rb ON rb.employee_id=x.recorded_by ORDER BY x.sold_on",
                    r => (Type: r.GetString(3), Region: r.GetString(9), On: Day(r, 7), Row: (object?[])[r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(4), r.GetString(5), r.GetDecimal(6), Day(r, 7), S(r, 8), r.GetString(9)]), token);
                cols = ["Sale", "Asset No.", "Type", "Buyer", "Username", "Price (SAR)", "Sold on", "Recorded by", "Region"];
                rows = raw.Where(x => InDates(x.On) && s.CanView(x.Type, x.Region) && (string.IsNullOrEmpty(region) || x.Region == region)).Select(x => x.Row).ToList();
                rows.Add(["Total", null, null, null, null, rows.Sum(r => (decimal)r[5]!), null, null, null]);
                break;
            }
            case "disposals":
            {
                var raw = await Rows(c, null, "SELECT x.disposal_no,a.asset_no,t.name_en,x.reason,x.status,x.requested_at_utc,db.display_name,x.completed_on,x.method,x.recipient,x.region_code FROM itam_data.disposal x " +
                    "JOIN itam_data.asset a ON a.asset_id=x.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code LEFT JOIN itam_access.employee db ON db.employee_id=x.decided_by ORDER BY x.disposal_id",
                    r => (At: Utc(r, 5), Region: r.GetString(10), Row: (object?[])[r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), DisposalStatusLabel(r.GetString(4)), Utc(r, 5), S(r, 6), Day(r, 7),
                        S(r, 8) is string m ? DisposalMethods.FirstOrDefault(x => x.Code == m).Label : null, S(r, 9), r.GetString(10)]), token);
                cols = ["Disposal", "Asset No.", "Type", "Reason", "Status", "Requested", "Decided by", "Disposed on", "Method", "Recipient", "Region"];
                rows = raw.Where(x => InDatesT(x.At) && (s.ViewsAll || s.LossView(x.Region)) && (string.IsNullOrEmpty(region) || x.Region == region)).Select(x => x.Row).ToList();
                break;
            }
            default: // loss
            {
                var raw = await Rows(c, null, "SELECT l.case_no,a.asset_no,t.name_en,ce.display_name,l.discovered_on,l.status,l.responsibility,l.decision,l.settlement_amount,l.writeoff,l.region_code FROM itam_data.loss_case l " +
                    "JOIN itam_data.asset a ON a.asset_id=l.asset_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code LEFT JOIN itam_access.employee ce ON ce.employee_id=l.custodian_id ORDER BY l.case_id",
                    r => (On: Day(r, 4), Region: r.GetString(10), Row: (object?[])[r.GetString(0), r.GetString(1), r.GetString(2), S(r, 3), Day(r, 4), LossStatusLabel(r.GetString(5)), ResponsibilityLabel(r.GetString(6)),
                        S(r, 7) switch { "NotLiable" => "Not liable", "Settlement" => "Settlement", _ => null }, r.IsDBNull(8) ? null : r.GetDecimal(8), WriteOffLabel(r.GetString(9)), r.GetString(10)]), token);
                cols = ["Case", "Asset No.", "Type", "Held by", "Discovered", "Status", "Responsibility", "Decision", "Settlement (SAR)", "Write-off", "Region"];
                rows = raw.Where(x => InDates(x.On) && s.LossView(x.Region) && (string.IsNullOrEmpty(region) || x.Region == region)).Select(x => x.Row).ToList();
                break;
            }
        }
        await Audit(c, null!, s.Id, "report", key, "Open", null, new { from, to, region, rows = rows.Count }, null, token);
        return new(key, def.Title, cols, rows, []);
    }

    public async Task<object> Dashboard(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(s.CanViewAny, "The dashboard is for IT.");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        var a = await AssetFacts(c, s, token);
        var live = a.Where(x => x.Status is not ("Disposed" or "Sold" or "Expired")).ToList();
        var months = Enumerable.Range(0, 6).Select(i => today.AddMonths(-5 + i)).Select(d => new DateOnly(d.Year, d.Month, 1)).ToList();
        var deliveries = (await Rows(c, null, "SELECT d.delivered_on,g.region_code,a.type_code FROM itam_data.delivery d JOIN itam_data.asset a ON a.asset_id=d.asset_id JOIN itam_ref.location g ON g.location_id=a.location_id WHERE d.delivered_on>=@f",
            r => (On: Day(r, 0)!.Value, Region: r.GetString(1), Type: r.GetString(2)), token, ("@f", DateParam(months[0])))).Where(x => s.CanView(x.Type, x.Region)).ToList();
        var yearStart = new DateOnly(today.Year, 1, 1);
        var maint = s.MaintenanceView ? Convert.ToDecimal(await Scalar(c, null, "SELECT COALESCE(SUM(COALESCE(final_cost,cost)),0) FROM itam_data.maintenance_order WHERE status IN(N'WithSupplier',N'Closed') AND created_at_utc>=@y", token, ("@y", DateParam(yearStart)))) : (decimal?)null;
        var ink = s.InkView || s.InvoiceToners ? Convert.ToInt32(await Scalar(c, null, "SELECT COALESCE(SUM(q.quantity),0) FROM itam_data.ink_delivery d JOIN itam_data.ink_request q ON q.request_id=d.request_id WHERE d.delivered_on>=@m", token,
            ("@m", DateParam(new DateOnly(today.Year, today.Month, 1))))) : (int?)null;
        var sales = s.SalesView ? Convert.ToDecimal(await Scalar(c, null, "SELECT COALESCE(SUM(price),0) FROM itam_data.sale WHERE sold_on>=@y", token, ("@y", DateParam(yearStart)))) : (decimal?)null;
        var alerts = await AlertItems(c, s, token);
        return new
        {
            tiles = new
            {
                assets = live.Count, inUse = live.Count(x => x.Status == "InUse"), inStore = live.Count(x => x.Status == "InStore"), inRepair = live.Count(x => x.Status == "InRepair"),
                inTransit = live.Count(x => x.Status == "InTransit"), pendingDisposal = live.Count(x => x.Status == "PendingDisposal"), lost = live.Count(x => x.Status == "Lost"),
                unsigned = live.Count(x => x.DeliveryNo is not null && !x.Signed), alerts = alerts.Sum(x => x.Count)
            },
            byStatus = StatusOrder.Select(st => new { code = st, label = ItamStatuses.Label(st), count = a.Count(x => x.Status == st) }).Where(x => x.count > 0),
            byType = live.GroupBy(x => x.Type).OrderByDescending(g => g.Count()).Select(g => new { label = g.Key, code = g.First().TypeCode, inUse = g.Count(x => x.Status == "InUse"), inStore = g.Count(x => x.Status == "InStore"), total = g.Count() }),
            byRegion = live.GroupBy(x => x.Region).OrderBy(g => g.Key).Select(g => new { label = g.Key, inUse = g.Count(x => x.Status == "InUse"), inStore = g.Count(x => x.Status == "InStore"), total = g.Count() }),
            stock = live.Where(x => x.Status == "InStore").GroupBy(x => x.KeptAt).OrderByDescending(g => g.Count()).Take(8).Select(g => new { label = g.Key, count = g.Count() }),
            ageing = AgeBands.Select(b => new { label = b, count = live.Count(x => AgeBand(x.Purchase, today) == b) }),
            computers = (await InStoreComputers(c, s, token)).GroupBy(x => x.Type).OrderByDescending(g => g.Count(x => !x.Reserved))
                .Select(g => new { label = g.Key, code = g.First().TypeCode, available = g.Count(x => !x.Reserved), reserved = g.Count(x => x.Reserved), total = g.Count() }),
            deliveries = months.Select(m => new { label = m.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture), count = deliveries.Count(d => d.On.Year == m.Year && d.On.Month == m.Month) }),
            money = new { maintenance = maint, sales, inkThisMonth = ink, year = today.Year },
            alerts = alerts.OrderByDescending(x => x.Urgent).ThenByDescending(x => x.Count).Take(6).Select(x => new { label = x.Label, count = x.Count, href = x.Href, urgent = x.Urgent })
        };
    }

    public async Task<ItamUpload> ReportExcel(ItamUser user, string key, DateOnly? from, DateOnly? to, string? region, CancellationToken token)
    {
        var r = await Report(user, key, from, to, region, token);
        var stamp = UtcNow.AddHours(3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return new($"IT Assets - {r.Title} ({stamp}).xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ItamXlsx.Build(r.Title, r.Columns, r.Rows));
    }
}
