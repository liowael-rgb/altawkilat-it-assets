using System.Text.Json;
using Itam.App;
using Microsoft.Data.SqlClient;
using PdfSharp.Pdf.IO;

// IT Assets checks, step 1: register, purchase orders, PO pack, roles, backups. LocalDB only, a uniquely named
// ITAM_Validation_ database (dropped at the end), invented people.
var passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAILED: " + name); passed++; }
async Task<bool> Throws<TEx>(Func<Task> f) where TEx : Exception { try { await f(); return false; } catch (TEx) { return true; } }
async Task<string> Error(Func<Task> f) { try { await f(); return ""; } catch (Exception ex) { return ex.Message; } }
string J(object o) => JsonSerializer.Serialize(o);

var name = $"ITAM_Validation_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid().ToString("N")[..8]}";
var master = new SqlConnectionStringBuilder { DataSource = @"(localdb)\MSSQLLocalDB", InitialCatalog = "master", IntegratedSecurity = true, TrustServerCertificate = true, Encrypt = true }.ConnectionString;
var cs = new SqlConnectionStringBuilder(master) { InitialCatalog = name }.ConnectionString;
async Task Exec(string sql) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand(sql, c) { CommandTimeout = 120 }; await q.ExecuteNonQueryAsync(); }
async Task<object?> Scalar(string sql) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand(sql, c); var v = await q.ExecuteScalarAsync(); return v is DBNull ? null : v; }
await using (var c = new SqlConnection(master)) { await c.OpenAsync(); using var q = new SqlCommand($"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET COMPATIBILITY_LEVEL=120;", c); await q.ExecuteNonQueryAsync(); }
try
{
    Check(Throws<InvalidOperationException>(() => { ItamSchemaInstaller.RequireTarget("FMMS_Production"); return Task.CompletedTask; }).Result, "Installer refuses other databases");
    await using (var c = new SqlConnection(cs)) Check(await ItamSchemaInstaller.Apply(c, name) == ItamSchemaInstaller.Count, "Migrations applied");
    await using (var c = new SqlConnection(cs)) Check(await ItamSchemaInstaller.Apply(c, name) == 0, "Second run applies nothing");
    var font = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"));
    var pdf = new ItamPdf(font, File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "calibri.ttf")), File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "calibrib.ttf")));
    var store = new ItamStore(() => new SqlConnection(cs), new FakeSecrets()) { Pdf = pdf };
    Guid G(int n) => new($"00000000-0000-0000-0000-{n:000000000000}");
    ItamUser U(int n) => new(G(n), "u" + n);

    // ---- Employees from Active Directory ---------------------------------------------------------------------------------------
    var people = Enumerable.Range(1, 12).Select(n => new ItamEmployee(G(n), "u" + n, "Person " + n, $"u{n}@example.invalid", null, null, null, true)).ToList();
    Check(await store.SyncEmployees(people, default) == (12, 0, 0), "Employees added from the directory read");
    Check(await Throws<InvalidOperationException>(() => store.SyncEmployees([], default)), "An empty directory read changes nothing");
    var changed = people.Select(p => p.Id == G(12) ? p with { Name = "Person Twelve" } : p).Where(p => p.Id != G(11)).ToList();
    Check(await store.SyncEmployees(changed, default) == (0, 1, 1) && await Scalar($"SELECT enabled FROM itam_access.employee WHERE employee_id='{G(11)}'") is false, "Changed name updated; missing account kept and disabled");
    Check(await Throws<ItamAccessDenied>(() => store.Me(U(11), default)) && await Throws<ItamAccessDenied>(() => store.Me(new ItamUser(Guid.NewGuid(), "x"), default)), "Disabled or unknown accounts are refused");
    Console.WriteLine("PASS employees");

    // ---- Roles and master data --------------------------------------------------------------------------------------------------
    await Exec($"""
        INSERT itam_access.role_assignment(employee_id,role_code,region_code,created_by) VALUES
         ('{G(1)}','AssetsOfficer',NULL,'{G(2)}'),('{G(2)}','ItManager',NULL,'{G(2)}'),('{G(3)}','TeamLeader',NULL,'{G(2)}'),('{G(4)}','SupportSpecialist','West','{G(2)}'),
         ('{G(5)}','SystemAdministrator',NULL,'{G(2)}'),('{G(6)}','VoiceAdministrator',NULL,'{G(2)}'),('{G(7)}','ItAdministrator',NULL,'{G(2)}'),('{G(8)}','Auditor',NULL,'{G(2)}');
        INSERT itam_ref.location(name_en,kind,region_code,city) VALUES(N'Riyadh WH','ITWarehouse','Central',N'Riyadh'),(N'Jeddah WH','ITWarehouse','West',N'Jeddah');
        INSERT itam_ref.supplier(name_en) VALUES(N'Supplier One');
        INSERT itam_ref.manufacturer(name_en) VALUES(N'Dell'),(N'Cisco');
        INSERT itam_ref.model(manufacturer_id,type_code,name_en) VALUES(1,'PC',N'Latitude'),(1,'MON',N'P24'),(2,'IP',N'8841');
        """);
    JsonElement E(object o) => JsonSerializer.SerializeToElement(o, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Check(await Throws<ItamAccessDenied>(() => store.Change(U(3), new("supplier", "save", E(new { name = "X", active = true }), null), default)), "Only the IT Administrator changes master data");
    await store.Change(U(7), new("supplier", "save", E(new { name = "Supplier Two", vat = "300000000000003", active = true }), "check"), default);
    Check(await Throws<ArgumentException>(() => store.Change(U(7), new("supplier", "save", E(new { name = "Bad VAT", vat = "123", active = true }), null), default)), "Supplier VAT number must be 15 digits");
    Check(await Throws<ArgumentException>(() => store.Change(U(2), new("role", "save", E(new { employee = G(9), role = "SupportSpecialist" }), null), default)), "An IT Support Specialist needs a region");
    await store.Change(U(2), new("role", "save", E(new { employee = G(9), role = "SupportSpecialist", region = "Central" }), null), default);
    Check(await Throws<ArgumentException>(() => store.Change(U(2), new("role", "save", E(new { employee = G(9), role = "SupportSpecialist", region = "Central" }), null), default)), "The same role twice is refused");
    Check(await Throws<ArgumentException>(() => store.Change(U(2), new("role", "save", E(new { employee = G(9), role = "InkOfficer" }), null), default)), "An Ink Officer needs a region");
    await store.Change(U(2), new("role", "save", E(new { employee = G(9), role = "InkOfficer", region = "Central" }), null), default);
    Check(await Throws<ItamAccessDenied>(() => store.Change(U(1), new("role", "save", E(new { employee = G(10), role = "Auditor" }), null), default)), "Only the IT Manager and IT Administrator give roles");
    Console.WriteLine("PASS roles and master data");

    // ---- Register ----------------------------------------------------------------------------------------------------------------
    ItamAssetInput A(string type, int loc, int model, string? serial, string? tag = null, string? mac = null, Dictionary<string, string?>? at = null, string? osKey = null, int mf = 1)
        => new(type, loc, mf, model, null, serial, tag, mac, null, "New", new DateOnly(2026, 1, 10), null, null, null, null, null, at, osKey, null);
    var pcAttrs = new Dictionary<string, string?> { ["ComputerType"] = "Notebook", ["OperatingSystem"] = "Win11", ["MemoryGb"] = "16", ["Processor"] = "Core i5", ["CapacityGb"] = "512" };
    var pc = JsonSerializer.SerializeToElement(await store.Register(U(1), A("PC", 1, 1, "SN-0001", "TAG1", null, pcAttrs, "AAAAA-BBBBB"), default));
    var mon = JsonSerializer.SerializeToElement(await store.Register(U(1), A("MON", 2, 2, "MN-0001"), default));
    Check(pc.GetProperty("assetNo").GetString() == "PC-4809" && mon.GetProperty("assetNo").GetString() == "MON-4810", "Asset No.: product code and one sequence for all types, after 4808");
    Check((await Error(() => store.Register(U(1), A("PC", 1, 1, null, "tag-1", null, pcAttrs), default))).Contains("already on PC-4809"), "Duplicate service tag refused (spaces, dashes and case ignored)");
    Check((await Error(() => store.Register(U(1), A("PC", 1, 1, "SN-0002", null, null, pcAttrs), default))).Contains("Service tag"), "Service tag required for computers");
    Check((await Error(() => store.Register(U(1), A("IP", 1, 3, "IP-0001", mf: 2), default))).Contains("MAC"), "MAC address required for IP phones");
    Check((await Error(() => store.Register(U(1), A("MON", 1, 2, null), default))).Contains("Serial"), "Serial number required (except software)");
    Check(await Throws<ArgumentException>(() => store.Register(U(1), A("PC", 1, 1, "SN-0003", "TAG3", null, new() { ["ComputerType"] = "Spaceship" }), default)), "List fields accept list values only");
    Check((await Error(() => store.Register(U(1), A("PC", 1, 1, null, "TAG-NOCPU", null, new() { ["ComputerType"] = "Notebook", ["MemoryGb"] = "16", ["CapacityGb"] = "512", ["OperatingSystem"] = "Win11" }), default))).Contains("Processor is required"),
        "Computers need processor, memory, storage and operating system");
    Check(await Throws<ArgumentException>(() => store.Register(U(1), A("MON", 1, 2, "MN-0009") with { PurchaseDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)) }, default)), "Purchase date cannot be in the future");
    var phone = JsonSerializer.SerializeToElement(await store.Register(U(6), A("IP", 1, 3, "IP-0002", mac: "aa-bb-cc-dd-ee-ff", mf: 2), default));
    Check(await Scalar($"SELECT mac_address FROM itam_data.asset WHERE asset_id={phone.GetProperty("id").GetInt32()}") as string == "AA:BB:CC:DD:EE:FF", "MAC address stored as AA:BB:CC:DD:EE:FF");
    Check(await Throws<ItamAccessDenied>(() => store.Register(U(4), A("MON", 1, 2, "MN-0100"), default)) && (await store.Register(U(4), A("MON", 2, 2, "MN-0101"), default)) is not null,
        "IT Support Specialist registers in own region only");
    Check(await Throws<ItamAccessDenied>(() => store.Register(U(8), A("MON", 2, 2, "MN-0102"), default)), "The Auditor cannot register");
    // A supplier not in the list is added while registering (old stock bought from a new supplier), and audited.
    var withSupplier = JsonSerializer.SerializeToElement(await store.Register(U(1), A("MON", 1, 2, "MN-0103") with { NewSupplier = "Check New Supplier", NewSupplierVat = "300000000000077" }, default));
    Check(await Scalar($"SELECT s.name_en+'|'+s.vat_no FROM itam_data.asset a JOIN itam_ref.supplier s ON s.supplier_id=a.supplier_id WHERE a.asset_id={withSupplier.GetProperty("id").GetInt32()}") as string == "Check New Supplier|300000000000077"
        && await Scalar("SELECT COUNT(*) FROM itam_access.audit WHERE entity='supplier' AND entity_key=N'Check New Supplier'") is int n1 && n1 == 1, "A new supplier is added while registering");
    Check(await Throws<ArgumentException>(() => store.Register(U(1), A("MON", 1, 2, "MN-0104") with { NewSupplier = "Bad VAT Co", NewSupplierVat = "123" }, default)), "A new supplier's VAT number must be 15 digits");
    // Software: no manufacturer, model or serial (the software name identifies it). Toner: no model (the toner code does).
    var sof = JsonSerializer.SerializeToElement(await store.Register(U(1), A("SOF", 1, 0, null, at: new() { ["Software"] = "Check Office" }) with { ManufacturerId = null, ModelId = null }, default));
    Check(await Scalar($"SELECT CASE WHEN manufacturer_id IS NULL AND model_id IS NULL AND serial_no IS NULL THEN 1 ELSE 0 END FROM itam_data.asset WHERE asset_id={sof.GetProperty("id").GetInt32()}") is int s1 && s1 == 1, "Software is registered without manufacturer and model");
    var ton = JsonSerializer.SerializeToElement(await store.Register(U(1), A("TON", 1, 0, null, at: new() { ["TonerCode"] = "W2210A" }) with { ModelId = null }, default));
    Check(await Scalar($"SELECT CASE WHEN manufacturer_id=1 AND model_id IS NULL THEN 1 ELSE 0 END FROM itam_data.asset WHERE asset_id={ton.GetProperty("id").GetInt32()}") is int t1 && t1 == 1
        && await Throws<ArgumentException>(() => store.Register(U(1), A("TON", 1, 0, null, at: new() { ["TonerCode"] = "W2211A" }) with { ManufacturerId = null, ModelId = null }, default)), "Toner is registered without a model, but needs its manufacturer");
    Console.WriteLine("PASS register");

    // ---- Who sees what -----------------------------------------------------------------------------------------------------------
    string List(int user) => J(store.Assets(U(user), new(null, null, null, null, null), default).Result);
    Check(List(4).Contains("MON-4810") && !List(4).Contains("PC-4809"), "Specialist West sees West assets only");
    Check(List(5).Contains("PC-4809") && !List(5).Contains("MON-4810"), "System Administrator sees computers only");
    Check(List(6).Contains("IP-") && !List(6).Contains("PC-4809"), "Voice Administrator sees IP phones only");
    Check(List(8).Contains("PC-4809") && List(8).Contains("MON-4810"), "Auditor sees all assets");
    Check(await Throws<ItamAccessDenied>(() => store.Asset(U(4), pc.GetProperty("id").GetInt32(), default)) && await Throws<ItamAccessDenied>(() => store.Assets(U(10), new(null, null, null, null, null), default)),
        "Assets of other regions, and employees without an IT role, are refused");
    Check(J(await store.Assets(U(1), new("tag1", null, null, null, null), default)).Contains("PC-4809"), "Search by service tag");

    // ---- Keys, edit, history -----------------------------------------------------------------------------------------------------
    var pcId = pc.GetProperty("id").GetInt32();
    Check(await Scalar($"SELECT protected_value FROM itam_data.asset_secret WHERE asset_id={pcId}") as string is { } enc && !enc.Contains("AAAAA"), "OS key stored encrypted");
    Check(await Throws<ItamAccessDenied>(() => store.ViewKey(U(3), pcId, "OsKey", default)), "Team Leader cannot see keys");
    Check(await store.ViewKey(U(1), pcId, "OsKey", default) == "AAAAA-BBBBB" && await Scalar($"SELECT COUNT(*) FROM itam_data.secret_view WHERE asset_id={pcId}") is 1, "Assets Officer sees the key; the view is logged");
    var detail = JsonSerializer.SerializeToElement(await store.Asset(U(1), pcId, default));
    var version = detail.GetProperty("asset").GetProperty("version").GetString();
    await store.Update(U(1), pcId, A("PC", 1, 1, "SN-0001", "TAG1", null, new() { ["ComputerType"] = "Notebook", ["OperatingSystem"] = "Win11", ["MemoryGb"] = "32", ["Processor"] = "Core i5", ["CapacityGb"] = "512" }) with { Version = version }, default);
    Check(await Scalar($"SELECT TOP 1 detail FROM itam_data.asset_event WHERE asset_id={pcId} AND kind='Edited'") as string is { } h && h.Contains("Memory (GB): 16 → 32"), "History records what changed");
    Check(await Throws<ItamConflict>(() => store.Update(U(1), pcId, A("PC", 1, 1, "SN-0001", "TAG1", null, pcAttrs) with { Version = version }, default)), "A stale edit is refused");
    var phoneId = phone.GetProperty("id").GetInt32();
    var pv = JsonSerializer.SerializeToElement(await store.Asset(U(6), phoneId, default)).GetProperty("asset").GetProperty("version").GetString();
    await store.Update(U(6), phoneId, A("IP", 1, 3, "CHANGED", mac: "11:22:33:44:55:66", mf: 2, at: new() { ["Extension"] = "5000" }) with { Version = pv }, default);
    // IP phones have no serial number (the MAC identifies them): a serial typed is not kept.
    Check(await Scalar($"SELECT ISNULL(serial_no,'none')+'|'+mac_address FROM itam_data.asset WHERE asset_id={phoneId}") as string == "none|11:22:33:44:55:66"
        && await Scalar($"SELECT value FROM itam_data.asset_attribute WHERE asset_id={phoneId} AND attr_code='Extension'") as string == "5000", "Voice Administrator changes MAC and extension only");
    Console.WriteLine("PASS who sees what, keys, edit, history");

    // ---- Purchase orders ---------------------------------------------------------------------------------------------------------
    var doc = pdf.Start(); doc.Text("Purchase order PO-77 (check document)", 40, doc.Y, 12); var poPdf = doc.Build();
    var poInput = new ItamPoInput("PO-77", new DateOnly(2026, 10, 1), 1, "Procurement", "pr-3", null, null,
        [new(1, "PC", 1, 1, null, "i7", 3, 1000m), new(2, "MON", 1, 2, null, null, 2, 500m)]);
    Check(await Throws<ItamAccessDenied>(() => store.CreatePo(U(4), poInput, new("po.pdf", "application/pdf", poPdf), default)), "Specialists cannot record POs");
    Check(await Throws<ArgumentException>(() => store.CreatePo(U(1), poInput, null, default)) && await Throws<ArgumentException>(() => store.CreatePo(U(1), poInput, new("po.png", "image/png", [0x89, 0x50, 0x4E, 0x47, 1, 2]), default)),
        "The PO document (PDF) is required");
    var po = JsonSerializer.SerializeToElement(await store.CreatePo(U(1), poInput, new("po.pdf", "application/pdf", poPdf), default)).GetProperty("id").GetInt32();
    Check(await Throws<ArgumentException>(() => store.CreatePo(U(3), poInput, new("po.pdf", "application/pdf", poPdf), default)), "The same PO number twice is refused");
    Check(await Scalar($"SELECT request_ref FROM itam_data.po WHERE po_id={po}") as string == "PR-3", "Request number stored as PR-n");
    int Line(int no) => (int)Scalar($"SELECT po_line_id FROM itam_data.po_line WHERE po_id={po} AND line_no={no}").Result!;
    ItamDeviceInput Dev(string? serial, string? tag = null) => new(serial, tag, null, null, "New", new() { ["ComputerType"] = "Notebook", ["Processor"] = "Core i7", ["MemoryGb"] = "16", ["CapacityGb"] = "512", ["OperatingSystem"] = "Win11" }, null, null, null);
    var r1 = JsonSerializer.SerializeToElement(await store.Receive(U(1), po, new(Line(1), 1, null, "INV-1", null, [Dev("POSN1", "POTAG1"), Dev("POSN2", "POTAG2")]), null, default));
    Check(r1.GetProperty("assets").GetArrayLength() == 2, "Partial receipt: 2 of 3");
    var before = await Scalar("SELECT COUNT(*) FROM itam_data.asset");
    Check((await Error(() => store.Receive(U(1), po, new(Line(1), 1, null, null, null, [Dev("POSN3", "POTAG3"), Dev("POSN4", "POTAG4")]), null, default))).Contains("still to come")
        && Equals(await Scalar("SELECT COUNT(*) FROM itam_data.asset"), before), "Receiving more than the remaining quantity is refused; nothing saved");
    Check((await Error(() => store.Receive(U(1), po, new(Line(2), 1, null, null, null, [new("SAME1", null, null, null, "New", null, null, null, null), new("same-1", null, null, null, "New", null, null, null, null)]), null, default))).Contains("twice")
        && Equals(await Scalar("SELECT COUNT(*) FROM itam_data.asset"), before), "The same serial twice in one receipt is refused; nothing saved");
    Check((await Error(() => store.Receive(U(1), po, new(Line(1), 1, null, null, null, [Dev(null, "TAG1")]), null, default))).Contains("Device 1"), "A service tag already in IT Assets is refused, naming the row");
    Check(await Scalar("SELECT COUNT(*) FROM itam_data.asset WHERE po_id IS NOT NULL AND unit_cost=1000 AND supplier_id=1 AND status='InStore'") is 2, "Received devices carry the PO cost and supplier, In Store");
    Check((await Error(() => store.Register(U(1), A("MON", 1, 2, "MN-0500") with { PoNumber = "PO-77" }, default))).Contains("receive the device on the PO"), "Registering by hand against a recorded PO is refused");
    var view = JsonSerializer.SerializeToElement(await store.Po(U(8), po, default), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var counts = view.GetProperty("counts");
    Check(counts.GetProperty("ordered").GetInt32() == 5 && counts.GetProperty("received").GetInt32() == 2 && counts.GetProperty("stillToCome").GetInt32() == 3 && counts.GetProperty("inStock").GetInt32() == 2, "PO counts");
    Check(view.GetProperty("totals").GetProperty("total").GetDecimal() == 4600m, "PO total with VAT 15%");
    var pov = view.GetProperty("po").GetProperty("version").GetString();
    Check((await Error(() => store.UpdatePo(U(1), po, poInput with { Version = pov, Lines = [new(1, "PC", 1, 1, null, null, 1, 1000m), new(2, "MON", 1, 2, null, null, 2, 500m)] }, null, default))).Contains("cannot be lower"),
        "A line cannot drop below what is received");
    Check((await Error(() => store.UpdatePo(U(1), po, poInput with { Version = pov, Lines = [new(2, "MON", 1, 2, null, null, 2, 500m)] }, null, default))).Contains("cannot be removed"), "A line with received devices cannot be removed");
    await store.UpdatePo(U(1), po, poInput with { Version = pov, InvoiceNo = "INV-1" }, null, default);
    Check(await Scalar($"SELECT invoice_no FROM itam_data.po WHERE po_id={po}") as string == "INV-1", "Invoice number added later");
    Console.WriteLine("PASS purchase orders");

    // ---- PO pack ------------------------------------------------------------------------------------------------------------------
    var pack = await store.PoPack(U(8), po, default);
    using (var ms = new MemoryStream(pack.Content)) using (var read = PdfReader.Open(ms, PdfDocumentOpenMode.Import))
        Check(read.PageCount == 2, "PO pack: report page + the PO document page");
    Check(await Scalar($"SELECT COUNT(*) FROM itam_data.pack_print WHERE po_id={po}") is 1, "Each print is recorded");
    var print = (int)(await Scalar($"SELECT print_id FROM itam_data.pack_print WHERE po_id={po}"))!;
    Check(await Throws<ItamAccessDenied>(() => store.MarkSent(U(8), po, print, null, default)), "The Auditor prints but cannot record the sending");
    await store.MarkSent(U(1), po, print, null, default);
    Check(await Scalar($"SELECT sent_on FROM itam_data.pack_print WHERE print_id={print}") is not null, "Sent to Accounting recorded");
    Check(await Throws<ItamAccessDenied>(() => store.PoPack(U(4), po, default)), "Specialists cannot print PO documents");
    var report = await store.PoReport(U(2), po, default);
    Check(System.Text.Encoding.ASCII.GetString(report.Content, 0, 5) == "%PDF-", "PO Delivery Report is a PDF");
    Console.WriteLine("PASS PO pack");

    // ---- Handover (build step 2) -------------------------------------------------------------------------------------------------
    var hpcAttrs = new Dictionary<string, string?> { ["ComputerType"] = "Notebook", ["Processor"] = "Core i5", ["MemoryGb"] = "8", ["CapacityGb"] = "256", ["OperatingSystem"] = "Win11" };
    var hpc = JsonSerializer.SerializeToElement(await store.Register(U(1), A("PC", 1, 1, "SN-H01", "TAGH01", null, hpcAttrs), default)).GetProperty("id").GetInt32();
    var hpc2 = JsonSerializer.SerializeToElement(await store.Register(U(1), A("PC", 1, 1, "SN-H02", "TAGH02", null, hpcAttrs), default)).GetProperty("id").GetInt32();
    var handDay = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
    ItamDeliveryInput Dl(Guid? who, string? computer) => new(who, handDay, "Personal", computer, null);
    Check(await Throws<ItamAccessDenied>(() => store.Deliver(U(4), hpc, Dl(G(9), "ATWK-H01"), default)) && await Throws<ItamAccessDenied>(() => store.Deliver(U(8), hpc, Dl(G(9), "ATWK-H01"), default)),
        "Specialists deliver in their regions only; the Auditor cannot deliver");
    Check(await Throws<ArgumentException>(() => store.Deliver(U(1), hpc, Dl(G(11), "ATWK-H01"), default)), "Deliveries go to active employees only");
    Check(await Throws<ArgumentException>(() => store.Deliver(U(1), hpc, Dl(G(9), null), default)) && await Throws<ArgumentException>(() => store.Deliver(U(1), hpc, Dl(G(9), "BAD NAME!"), default)),
        "A computer needs a valid computer name");
    var dn1 = JsonSerializer.SerializeToElement(await store.Deliver(U(1), hpc, Dl(G(9), "atwk-h01"), default));
    var dn1Id = dn1.GetProperty("id").GetInt32();
    Check(dn1.GetProperty("deliveryNo").GetString()!.EndsWith("-4133", StringComparison.Ordinal)
        && await Scalar($"SELECT status+'|'+stock_location FROM itam_data.asset WHERE asset_id={hpc}") as string == "InUse|Employee"
        && await Scalar($"SELECT value FROM itam_data.asset_attribute WHERE asset_id={hpc} AND attr_code='ComputerName'") as string == "ATWK-H01",
        "Delivery note Asset No.-4133; the asset is In Use with the employee; computer name kept");
    Check(await Throws<ArgumentException>(() => store.Deliver(U(1), hpc, Dl(G(10), "ATWK-H09"), default)), "One open custody per asset");
    Check(await Throws<ArgumentException>(() => store.Deliver(U(1), hpc2, Dl(G(10), "ATWK-H01"), default)), "A computer name on an open delivery cannot be reused");
    var note = await store.DeliveryNote(U(8), dn1Id, default);
    Check(System.Text.Encoding.ASCII.GetString(note.Content, 0, 5) == "%PDF-", "Delivery note PDF (the Auditor can read it)");
    var scanPage = pdf.Start(); scanPage.Text("signed", 40, scanPage.Y, 12); var scan = new ItamUpload("signed.pdf", "application/pdf", scanPage.Build());
    Check(await Throws<ItamAccessDenied>(() => store.UploadSigned(U(8), dn1Id, scan, default)), "The Auditor cannot upload signed copies");
    await store.UploadSigned(U(1), dn1Id, scan, default);
    Check(await Throws<ArgumentException>(() => store.UploadSigned(U(1), dn1Id, scan, default)), "The signed copy is kept unchanged once uploaded");
    Check(await Throws<ArgumentException>(() => store.Assess(U(1), dn1Id, new("x", "Reuse"), null, default)), "No technical assessment while the employee still has the asset");
    var ret = JsonSerializer.SerializeToElement(await store.ReturnAsset(U(1), dn1Id, new(null, "Faulty", 2, "Broken screen"), default));
    Check(ret.GetProperty("receiptNo").GetString() == dn1.GetProperty("deliveryNo").GetString() + "-R"
        && await Scalar($"SELECT status+'|'+stock_location+'|'+CAST(location_id AS varchar(9)) FROM itam_data.asset WHERE asset_id={hpc}") as string == "InStore|ITWarehouse|2"
        && await Scalar($"SELECT COUNT(*) FROM itam_data.asset_attribute WHERE asset_id={hpc} AND attr_code='ComputerName'") is 0,
        "Receipt note -R: the asset is In Store where it is received; computer name freed");
    Check(await Throws<ArgumentException>(() => store.ReturnAsset(U(1), dn1Id, new(null, "Working", 1, null), default)), "A delivery is received back once");
    await store.Deliver(U(1), hpc2, Dl(G(10), "ATWK-H01"), default);
    Check(await Scalar($"SELECT COUNT(*) FROM itam_data.delivery WHERE computer_name='ATWK-H01'") is 2, "The freed computer name can be used again");
    var adTask = (int)(await Scalar($"SELECT task_id FROM itam_data.task WHERE delivery_id={dn1Id} AND kind='RemoveFromAd'"))!;
    Check(await Throws<ItamAccessDenied>(() => store.TaskDone(U(6), adTask, new(null), default)), "Only the System Administrator confirms the AD and Endpoint Central removal");
    await store.TaskDone(U(5), adTask, new("Removed"), default);
    Check(await Throws<ArgumentException>(() => store.TaskDone(U(5), adTask, new(null), default)), "A task is done once");
    await store.Assess(U(1), dn1Id, new("Screen and board damaged.", "Dispose"), null, default);
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={hpc}") as string == "PendingDisposal"
        && await Throws<ArgumentException>(() => store.Assess(U(1), dn1Id, new("again", "Reuse"), null, default))
        && System.Text.Encoding.ASCII.GetString((await store.AssessmentNote(U(1), dn1Id, default)).Content, 0, 5) == "%PDF-"
        && System.Text.Encoding.ASCII.GetString((await store.ReceiptNote(U(1), dn1Id, default)).Content, 0, 5) == "%PDF-",
        "Technical assessment TA-: Dispose starts Pending disposal approval; receipt and assessment PDFs");
    var phoneDn = JsonSerializer.SerializeToElement(await store.Deliver(U(1), phoneId, Dl(G(9), null), default)).GetProperty("id").GetInt32();
    await store.ReturnAsset(U(1), phoneDn, new(null, "Working", 1, null), default);
    var cucm = (int)(await Scalar($"SELECT task_id FROM itam_data.task WHERE delivery_id={phoneDn} AND kind='RemoveFromCucm'"))!;
    Check(await Throws<ItamAccessDenied>(() => store.TaskDone(U(5), cucm, new(null), default)), "Only the Voice Administrator confirms the CUCM removal");
    await store.TaskDone(U(6), cucm, new(null), default);
    var sysTasks = JsonSerializer.SerializeToElement(await store.MyTasks(U(5), default)).GetProperty("tasks").EnumerateArray().ToList();
    var voiceTasks = JsonSerializer.SerializeToElement(await store.MyTasks(U(6), default)).GetProperty("tasks").EnumerateArray().ToList();
    Check(sysTasks.Count > 0 && sysTasks.All(t => t.GetProperty("kind").GetString() == "RemoveFromAd") && voiceTasks.Count > 0 && voiceTasks.All(t => t.GetProperty("kind").GetString() == "RemoveFromCucm")
        && await Throws<ItamAccessDenied>(() => store.MyTasks(U(4), default)), "Tasks page: the System Administrator sees computer removals, the Voice Administrator extension removals");
    Check(await Throws<ItamAccessDenied>(() => store.LabelPdf(U(4), hpc2, default)) && await Throws<ItamAccessDenied>(() => store.LabelPdf(U(8), hpc2, default)), "Labels: own regions for specialists; not the Auditor");
    var label = await store.LabelPdf(U(7), hpc2, default); var zpl = System.Text.Encoding.UTF8.GetString((await store.LabelZpl(U(1), hpc2, default)).Content);
    Check(System.Text.Encoding.ASCII.GetString(label.Content, 0, 5) == "%PDF-" && zpl.Contains("^BQN", StringComparison.Ordinal) && zpl.Contains("TAGH02", StringComparison.Ordinal), "Label as PDF and Zebra ZPL (QR code, Asset No., service tag)");
    // The PO pack carries the delivery notes: unsigned ones marked, signed ones as uploaded.
    var poDevice = (int)(await Scalar($"SELECT TOP 1 asset_id FROM itam_data.asset WHERE po_id={po} AND status='InStore' ORDER BY asset_seq"))!;
    var poIsPc = await Scalar($"SELECT type_code FROM itam_data.asset WHERE asset_id={poDevice}") as string == "PC";
    var poDn = JsonSerializer.SerializeToElement(await store.Deliver(U(1), poDevice, Dl(G(10), poIsPc ? "ATWK-PO1" : null), default)).GetProperty("id").GetInt32();
    var pack2 = await store.PoPack(U(1), po, default);
    using (var ms = new MemoryStream(pack2.Content)) using (var read = PdfReader.Open(ms, PdfDocumentOpenMode.Import)) Check(read.PageCount == 3, "PO pack adds the delivery note (not yet signed)");
    await store.UploadSigned(U(1), poDn, scan, default);
    await store.PoPack(U(1), po, default);
    var poCounts = JsonSerializer.SerializeToElement(await store.Po(U(1), po, default)).GetProperty("counts");
    Check(await Scalar($"SELECT CAST(notes_unsigned AS varchar(5))+'|'+CAST(notes_signed AS varchar(5)) FROM itam_data.pack_print WHERE print_id=(SELECT MIN(print_id) FROM itam_data.pack_print WHERE po_id={po} AND notes_unsigned=1)") as string == "1|0"
        && await Scalar($"SELECT notes_signed FROM itam_data.pack_print WHERE print_id=(SELECT MAX(print_id) FROM itam_data.pack_print WHERE po_id={po})") is 1
        && poCounts.GetProperty("deliveredSigned").GetInt32() == 1, "Pack prints record signed and unsigned notes; PO counts follow the signed copy");
    var hv = JsonSerializer.SerializeToElement(await store.Handover(U(4), "all", null, default));
    Check(hv.GetProperty("items").EnumerateArray().All(x => x.GetProperty("region").GetString() == "West"), "The Handover page shows a specialist his own regions");
    Check(await Throws<ItamAccessDenied>(() => store.Handover(U(10), "all", null, default)), "People without an IT role cannot open the Handover page");
    Console.WriteLine("PASS handover");

    // ---- Ink and invoices (build step 3a) ----------------------------------------------------------------------------------------
    await Exec($"INSERT itam_ref.model(manufacturer_id,type_code,name_en) VALUES(1,'PNT',N'LaserJet'); UPDATE itam_access.employee SET manager_id='{G(12)}' WHERE employee_id='{G(10)}';");
    var pntModel = (int)(await Scalar("SELECT model_id FROM itam_ref.model WHERE type_code='PNT'"))!;
    var printer = JsonSerializer.SerializeToElement(await store.Register(U(1), A("PNT", 1, pntModel, "PR-001", at: new() { ["PrinterType"] = "Network", ["TonerCode"] = "W2210A" }), default)).GetProperty("id").GetInt32();
    Check(await Throws<ArgumentException>(() => store.CreateInkRequest(U(10), new(printer, null, 1, null, null), default)), "Ink only for an active printer (In Use)");
    await store.Deliver(U(1), printer, new(G(10), handDay, "Department", null, null), default);
    ItamInkRequestInput Ink(int qty) => new(printer, null, qty, null, null);
    var ink1 = JsonSerializer.SerializeToElement(await store.CreateInkRequest(U(10), Ink(2), default)).GetProperty("id").GetInt32();
    Check(await Scalar($"SELECT toner+'|'+status+'|'+request_no FROM itam_data.ink_request WHERE request_id={ink1}") as string == "W2210A|Submitted|INK-1"
        && await Throws<ItamAccessDenied>(() => store.DecideInk(U(12), ink1, new(true, null), default)), "Ink request INK-1 with the printer toner, waiting for the ink officer");
    // The ink officer (IT of the printer's region) chooses the approving manager: a senior manager, not the AD manager.
    Check(await Throws<ItamAccessDenied>(() => store.SendInkForApproval(U(4), ink1, new(G(12)), default)) && await Throws<ItamAccessDenied>(() => store.SendInkForApproval(U(3), ink1, new(G(12)), default))
        && await Throws<ArgumentException>(() => store.SendInkForApproval(U(9), ink1, new(G(10)), default))
        && await Throws<ArgumentException>(() => store.SendInkForApproval(U(9), ink1, new(G(11)), default)), "Only the region's Ink Officer sends (not a specialist or the Team Leader); the approver is an active employee other than the requester");
    await store.SendInkForApproval(U(9), ink1, new(G(12)), default);
    Check(JsonSerializer.SerializeToElement(await store.Me(U(12), default)).GetProperty("pendingApprovals").GetInt32() == 1
        && await Throws<ArgumentException>(() => store.SendInkForApproval(U(9), ink1, new(G(12)), default)) is false, "The chosen manager sees the request; it can be re-sent while it waits");
    Check(await Throws<ItamAccessDenied>(() => store.DecideInk(U(10), ink1, new(true, null), default)) && await Throws<ItamAccessDenied>(() => store.DecideInk(U(9), ink1, new(true, null), default)),
        "Only the chosen manager (or backup) decides; nobody approves their own request");
    await store.DecideInk(U(12), ink1, new(true, null), default);
    var ink2 = JsonSerializer.SerializeToElement(await store.CreateInkRequest(U(10), Ink(2), default)).GetProperty("id").GetInt32();
    await store.SendInkForApproval(U(9), ink2, new(G(12)), default);
    await Exec($"INSERT itam_access.leave_backup(person_id,backup_employee_id,leave_from,leave_to,set_by) VALUES('{G(12)}','{G(3)}',CAST(DATEADD(day,-1,SYSUTCDATETIME()) AS date),CAST(DATEADD(day,3,SYSUTCDATETIME()) AS date),'{G(12)}')");
    await store.DecideInk(U(3), ink2, new(true, null), default);
    var ink3 = JsonSerializer.SerializeToElement(await store.CreateInkRequest(U(10), Ink(1), default)).GetProperty("id").GetInt32();
    await store.SendInkForApproval(U(9), ink3, new(G(12)), default);
    Check(await Throws<ArgumentException>(() => store.DecideInk(U(12), ink3, new(false, null), default)), "A rejection needs a reason");
    await store.DecideInk(U(12), ink3, new(false, "Use the shared printer"), default);
    Check(await Scalar($"SELECT status FROM itam_data.ink_request WHERE request_id={ink2}") as string == "Approved" && await Scalar($"SELECT status FROM itam_data.ink_request WHERE request_id={ink3}") as string == "Rejected"
        && await Throws<ArgumentException>(() => store.DeliverInk(U(9), ink3, new(null, "Shelf", null), default)), "The manager backup on leave approves; rejected requests are not delivered");
    Check(await Throws<ItamAccessDenied>(() => store.CreateOrder(U(4), new(1, [ink1], null), default)), "Orders for your regions only");
    var order = JsonSerializer.SerializeToElement(await store.CreateOrder(U(9), new(1, [ink1], "test"), default));
    var orderId = order.GetProperty("id").GetInt32();
    Check(order.GetProperty("orderNo").GetString() == "ORD-1" && await Throws<ArgumentException>(() => store.CreateOrder(U(9), new(1, [ink1], null), default))
        && System.Text.Encoding.ASCII.GetString((await store.OrderPdf(U(9), orderId, default)).Content, 0, 5) == "%PDF-", "Supplier order ORD-1 with its PDF; a request is ordered once");
    var invDoc = pdf.Start(); invDoc.Text("invoice", 40, invDoc.Y, 12); var invPdf = new ItamUpload("invoice.pdf", "application/pdf", invDoc.Build());
    var inv = JsonSerializer.SerializeToElement(await store.CreateInvoice(U(9), new(1, "INV-T1", handDay, "Toners", orderId, null, null, null, [new("HP 207A W2210A Black", 3, 291m)]), invPdf, default)).GetProperty("id").GetInt32();
    Check(await Scalar($"SELECT CAST(total AS varchar(20)) FROM itam_data.invoice WHERE invoice_id={inv}") as string == "1003.95"
        && await Throws<ArgumentException>(() => store.CreateInvoice(U(9), new(1, "INV-T1", handDay, "Toners", null, null, null, null, [new("x", 1, 1m)]), invPdf, default))
        && await Throws<ItamAccessDenied>(() => store.CreateInvoice(U(9), new(1, "INV-D1", handDay, "Devices", null, po, null, null, [new("x", 1, 1m)]), invPdf, default)),
        "Toner invoice with VAT 15%; one number per supplier; device invoices by the Team Leader or IT Assets Officer only");
    var receipt = (int)(await Scalar($"SELECT TOP 1 receipt_id FROM itam_data.po_receipt WHERE po_id={po}"))!;
    var poSupplier = (int)(await Scalar($"SELECT supplier_id FROM itam_data.po WHERE po_id={po}"))!;
    await store.CreateInvoice(U(1), new(poSupplier, "INV-D1", handDay, "Devices", null, po, [receipt], null, [new("Devices", 1, 100m)]), invPdf, default);
    Check(await Scalar($"SELECT COUNT(*) FROM itam_data.po_receipt WHERE receipt_id={receipt} AND invoice_id IS NOT NULL") is 1
        && await Throws<ArgumentException>(() => store.CreateInvoice(U(1), new(poSupplier, "INV-D2", handDay, "Devices", null, po, [receipt], null, [new("Devices", 1, 100m)]), invPdf, default)),
        "A device invoice covers PO receipts; a receipt is on one invoice only");
    var line = (int)(await Scalar($"SELECT line_id FROM itam_data.invoice_line WHERE invoice_id={inv}"))!;
    var inkDone = JsonSerializer.SerializeToElement(await store.DeliverInk(U(9), ink1, new(null, "Invoice", line), default));
    Check(inkDone.GetProperty("requestNo").GetString() == "INK-1" && await Scalar($"SELECT COUNT(*) FROM itam_data.ink_delivery WHERE request_id={ink1} AND invoice_line_id={line}") is 1
        && await Throws<ArgumentException>(() => store.DeliverInk(U(9), ink2, new(null, "Invoice", line), default)), "Ink delivered from the invoice line (no delivery note); never more than the line holds");
    Check(await Throws<ArgumentException>(() => store.DeliverInk(U(9), ink2, new(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)).AddDays(-1), "Shelf", null), default)), "Ink is not delivered before its approval date");
    await store.DeliverInk(U(9), ink2, new(null, "Shelf", null), default);
    Check(System.Text.Encoding.ASCII.GetString((await store.InkRequestPdf(U(10), ink1, default)).Content, 0, 5) == "%PDF-" && (await store.InkRequestPdf(U(12), ink1, default)).Content.Length > 0
        && await Throws<ItamAccessDenied>(() => store.InkRequestPdf(U(4), ink1, default)), "The employee and the approver read the ink request document; other regions cannot");
    Check(await Scalar($"SELECT CASE WHEN sent_by='{G(9)}' AND sent_at_utc IS NOT NULL THEN 1 ELSE 0 END FROM itam_data.ink_request WHERE request_id={ink1}") is 1, "Who sent the request for approval is recorded");
    var payPack = JsonSerializer.SerializeToElement(await store.CreatePack(U(9), new(1, "Toners", [inv]), default)).GetProperty("id").GetInt32();
    var packFile = await store.PackPdf(U(9), payPack, default);
    using (var ms = new MemoryStream(packFile.Content)) using (var read = PdfReader.Open(ms, PdfDocumentOpenMode.Import))
        Check(read.PageCount == 3, "Payment pack: cover memo, invoice and the ink request document with its approval");
    Check(await Throws<ArgumentException>(() => store.CreatePack(U(9), new(1, "Toners", [inv]), default)) && await Throws<ArgumentException>(() => store.PackDates(U(9), payPack, new(null, handDay, null), default)),
        "An invoice is in one pack; paid only after it went to Accounting");
    await store.PackDates(U(9), payPack, new(handDay, null, null), default); await store.PackDates(U(9), payPack, new(null, handDay, "Ref 123"), default);
    Check(await Scalar($"SELECT CASE WHEN sent_on IS NOT NULL AND paid_on IS NOT NULL THEN 1 ELSE 0 END FROM itam_data.payment_pack WHERE pack_id={payPack}") is 1, "Sent to Accounting and paid dates recorded");
    // Supplier module: the Ink Officer and IT Assets Officer add suppliers; picked invoices of one supplier go in one pack.
    Check(await Throws<ItamAccessDenied>(() => store.SaveSupplier(U(10), null, new("Toner House", null, null, null, null, null, null, null), default))
        && await Throws<ArgumentException>(() => store.SaveSupplier(U(9), null, new("Toner House", "123", null, null, null, null, null, null), default))
        && await Throws<ArgumentException>(() => store.SaveSupplier(U(9), null, new("Supplier One", null, null, null, null, null, null, null), default)), "Only IT supplier roles add suppliers; VAT 15 digits; names unique");
    var house = JsonSerializer.SerializeToElement(await store.SaveSupplier(U(9), null, new("Toner House", "300000000000099", "1010101010", "Sami", "0551234567", "orders@example.invalid", "Riyadh", null), default)).GetProperty("id").GetInt32();
    await store.SaveSupplier(U(1), house, new("Toner House Co.", "300000000000099", null, "Sami", "0551234567", null, null, null), default);
    var supList = JsonSerializer.SerializeToElement(await store.Suppliers(U(9), default));
    var sup1 = JsonSerializer.SerializeToElement(await store.Supplier(U(1), 1, default));
    Check(supList.GetProperty("suppliers").EnumerateArray().Any(x => x.GetProperty("name").GetString() == "Toner House Co.") && sup1.GetProperty("invoices").GetArrayLength() >= 2,
        "Suppliers list and one supplier's invoices");
    var dInv = (int)(await Scalar("SELECT invoice_id FROM itam_data.invoice WHERE invoice_no=N'INV-D1'"))!;
    var pInv = JsonSerializer.SerializeToElement(await store.CreateInvoice(U(1), new(1, "INV-P1", handDay, "PartsMaintenance", null, null, null, null, [new("Fuser unit", 1, 100m)]), invPdf, default)).GetProperty("id").GetInt32();
    var hInv = JsonSerializer.SerializeToElement(await store.CreateInvoice(U(9), new(house, "H-1", handDay, "Toners", null, null, null, null, [new("Toner", 1, 50m)]), invPdf, default)).GetProperty("id").GetInt32();
    Check(await Throws<ArgumentException>(() => store.CreatePack(U(1), new(1, null, [dInv, hInv]), default))
        && await Throws<ItamAccessDenied>(() => store.CreatePack(U(9), new(1, null, [dInv, pInv]), default)), "One pack holds one supplier's invoices; each category needs its right");
    var mixed = JsonSerializer.SerializeToElement(await store.CreatePack(U(1), new(1, null, [dInv, pInv]), default)).GetProperty("id").GetInt32();
    Check(await Scalar($"SELECT category FROM itam_data.payment_pack WHERE pack_id={mixed}") as string == "Mixed"
        && System.Text.Encoding.ASCII.GetString((await store.PackPdf(U(1), mixed, default)).Content, 0, 5) == "%PDF-", "Picked invoices of different categories make a Mixed pack");
    // Maintenance and the asset history; search by computer details and by the employee who has the asset.
    Check(await Throws<ItamAccessDenied>(() => store.OpenMaintenance(U(4), printer, new("Repair", null, "Jam", "IT", null, true), default))
        && await Throws<ArgumentException>(() => store.OpenMaintenance(U(9), printer, new("Repair", null, "Jam", "Supplier", null, true), default)), "Maintenance by IT of the region; a supplier job names the supplier");
    var mt = JsonSerializer.SerializeToElement(await store.OpenMaintenance(U(9), printer, new("Repair", null, "Paper jam", "IT", null, true), default)).GetProperty("id").GetInt32();
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={printer}") as string == "InRepair"
        && await Throws<ArgumentException>(() => store.OpenMaintenance(U(9), printer, new("Preventive", null, "x", "IT", null, false), default)), "Taken in: In Repair; one open maintenance per asset");
    await store.CloseMaintenance(U(9), mt, new(null, "Roller cleaned", null, 0m, pInv), default);
    var hist = JsonSerializer.SerializeToElement(await store.Asset(U(1), printer, default)).GetProperty("record");
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={printer}") as string == "InUse" && hist.GetProperty("maintenance").GetArrayLength() == 1
        && hist.GetProperty("maintenance")[0].GetProperty("invoice").GetString() == "INV-P1", "Closing returns the asset to its status; the history lists the maintenance with its invoice");
    var ctype = await Scalar("SELECT TOP 1 value FROM itam_data.asset_attribute WHERE attr_code=N'ComputerType'") as string;
    var holder = await Scalar("SELECT TOP 1 e.account FROM itam_data.delivery d JOIN itam_access.employee e ON e.employee_id=d.employee_id WHERE d.received_at_utc IS NULL") as string;
    await Exec($"UPDATE itam_access.employee SET sap_id=N'50001' WHERE account=N'{holder}'");
    var byType = JsonSerializer.SerializeToElement(await store.Assets(U(1), new(null, null, null, null, null, 1, ComputerType: ctype), default)).GetProperty("items");
    var byUser = JsonSerializer.SerializeToElement(await store.Assets(U(1), new(null, null, null, null, null, 1, User: holder), default)).GetProperty("items");
    var bySap = JsonSerializer.SerializeToElement(await store.Assets(U(1), new(null, null, null, null, null, 1, SapId: "50001"), default)).GetProperty("items");
    Check(ctype is not null && byType.GetArrayLength() > 0 && byType.EnumerateArray().All(x => x.GetProperty("type").GetString() == "PC")
        && byUser.GetArrayLength() > 0 && byUser.EnumerateArray().All(x => x.GetProperty("username").GetString() == holder) && bySap.GetArrayLength() == byUser.GetArrayLength(),
        "Search by computer type, by the employee's username and by SAP ID");
    // A printer not in IT Assets: serial number and model typed by the employee, region and office from Active Directory, linked or
    // registered by the Ink Officer before it goes for approval.
    await Exec($"UPDATE itam_access.employee SET branch=N'Riyadh WH',region=N'Central' WHERE employee_id='{G(10)}'");
    Check(await Throws<ArgumentException>(() => store.CreateInkRequest(U(10), new(null, null, 1, "T1", null), default))
        && await Throws<ArgumentException>(() => store.CreateInkRequest(U(10), new(null, null, 1, "T1", null, "SN-NEW-1"), default)), "A printer not in IT Assets needs its serial number and model");
    var newInk = JsonSerializer.SerializeToElement(await store.CreateInkRequest(U(10), new(null, null, 1, "T1", null, "SN-NEW-1", null, "Brother HL-L2350"), default)).GetProperty("id").GetInt32();
    Check(await Scalar($"SELECT ISNULL(region_code,'-')+'|'+employee_location+'|'+CAST(CASE WHEN printer_asset_id IS NULL THEN 0 ELSE 1 END AS varchar(1)) FROM itam_data.ink_request WHERE request_id={newInk}") as string == "Central|Riyadh WH|0"
        && await Throws<ArgumentException>(() => store.SendInkForApproval(U(9), newInk, new(G(12)), default)), "Region from Active Directory, office as the printer location; not sent for approval before the printer is linked");
    Check(await Throws<ItamAccessDenied>(() => store.RegisterInkPrinter(U(4), newInk, new(1, 1, null, "Brother HL-L2350", "SN-NEW-1", "T1", null, "Used"), default)), "Only the region's Ink Officer registers the printer");
    var reg = JsonSerializer.SerializeToElement(await store.RegisterInkPrinter(U(9), newInk, new(1, 1, null, "Brother HL-L2350", "SN-NEW-1", "T1", "Room 2", "Used"), default));
    var regId = reg.GetProperty("id").GetInt32();
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={regId}") as string == "InUse" && await Scalar($"SELECT printer_asset_id FROM itam_data.ink_request WHERE request_id={newInk}") is int linked && linked == regId,
        "The Ink Officer registers the printer (In Use, old stock) and the request is linked");
    await store.SendInkForApproval(U(9), newInk, new(G(12)), default);
    var again = JsonSerializer.SerializeToElement(await store.CreateInkRequest(U(10), new(null, null, 1, "T1", null, "sn-new-1", null, "x"), default)).GetProperty("id").GetInt32();
    var other = JsonSerializer.SerializeToElement(await store.CreateInkRequest(U(10), new(null, null, 1, "T1", null, "SN-OTHER", null, "x"), default)).GetProperty("id").GetInt32();
    await store.LinkInkPrinter(U(9), other, new(printer), default);
    Check(await Scalar($"SELECT printer_asset_id FROM itam_data.ink_request WHERE request_id={again}") is int a2 && a2 == regId
        && await Scalar($"SELECT printer_asset_id FROM itam_data.ink_request WHERE request_id={other}") is int a3 && a3 == printer
        && await Throws<ArgumentException>(() => store.LinkInkPrinter(U(9), other, new(printer), default)), "A known serial number links itself; the Ink Officer links the others once");
    Console.WriteLine("PASS ink and invoices");

    // ---- My Backup ----------------------------------------------------------------------------------------------------------------
    var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
    await store.SetBackup(U(1), new(null, G(3), today.AddDays(1), today.AddDays(5), "Annual leave"), default);
    Check(await Throws<ArgumentException>(() => store.SetBackup(U(1), new(null, G(4), today.AddDays(3), today.AddDays(8), null), default)), "Overlapping backups are refused");
    Check(await Throws<ArgumentException>(() => store.SetBackup(U(3), new(null, G(1), today.AddDays(2), today.AddDays(3), null), default)), "A backup on leave the same dates cannot be chosen");
    Check(await Throws<ArgumentException>(() => store.SetBackup(U(1), new(null, G(1), today.AddDays(10), today.AddDays(11), null), default)), "You cannot be your own backup");
    Check(await Throws<ItamAccessDenied>(() => store.SetBackup(U(1), new(G(3), G(4), today.AddDays(10), today.AddDays(11), null), default)), "Only the IT Manager and IT Administrator set backups for others");
    var mine = JsonSerializer.SerializeToElement(await store.Backups(U(1), default)).GetProperty("items")[0];
    Check(mine.GetProperty("status").GetString() == "Upcoming", "Backup shown as upcoming");
    await store.CancelBackup(U(1), mine.GetProperty("id").GetInt32(), default);
    Check(J(await store.Backups(U(2), default)).Contains("Cancelled"), "Cancelled backup stays in the IT Manager's list");
    Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM itam_access.audit")) >= 8, "Changes are in the audit log");
    Console.WriteLine("PASS backups and audit");
    Console.WriteLine($"IT Assets checks passed: {passed}");
}
finally
{
    SqlConnection.ClearAllPools();
    await using var c = new SqlConnection(master); await c.OpenAsync();
    using var q = new SqlCommand($"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END", c); await q.ExecuteNonQueryAsync();
}

sealed class FakeSecrets : IItamSecrets
{
    public string Protect(string value) => "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string(value.Reverse().ToArray())));
    public string Unprotect(string value) => new(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value[4..])).Reverse().ToArray());
}
