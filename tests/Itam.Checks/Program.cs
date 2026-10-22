using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    // The store runs with the rights of the production site only (role itam_runtime, as the IIS application pool identity),
    // so a statement the site may not run fails here, not in production.
    await using (var c = new SqlConnection(cs)) { await c.OpenAsync(); using var q = new SqlCommand("CREATE USER itam_check_runtime WITHOUT LOGIN; ALTER ROLE itam_runtime ADD MEMBER itam_check_runtime; GRANT SELECT ON dbo.itam_schema_migration TO itam_check_runtime;", c); await q.ExecuteNonQueryAsync(); }
    SqlConnection Runtime()
    {
        // No pooling: the impersonation must not survive into a reused connection.
        var rc = new SqlConnection(new SqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
        rc.StateChange += (_, e) => { if (e.CurrentState == System.Data.ConnectionState.Open) { using var q = rc.CreateCommand(); q.CommandText = "EXECUTE AS USER=N'itam_check_runtime'"; q.ExecuteNonQuery(); } };
        return rc;
    }
    await using (var rc = Runtime()) { await rc.OpenAsync(); using var q = rc.CreateCommand(); q.CommandText = "SELECT USER_NAME()"; Check(await q.ExecuteScalarAsync() as string == "itam_check_runtime", "The store runs as the site's runtime role in the checks"); }
    var store = new ItamStore(Runtime, new FakeSecrets()) { Pdf = pdf };
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
         ('{G(5)}','SystemAdministrator',NULL,'{G(2)}'),('{G(6)}','VoiceAdministrator',NULL,'{G(2)}'),('{G(7)}','ItManager',NULL,'{G(2)}');
        INSERT itam_ref.location(name_en,kind,region_code,city) VALUES(N'Riyadh WH','ITWarehouse','Central',N'Riyadh'),(N'Jeddah WH','ITWarehouse','West',N'Jeddah');
        INSERT itam_ref.supplier(name_en) VALUES(N'Supplier One');
        INSERT itam_ref.manufacturer(name_en) VALUES(N'Dell'),(N'Cisco');
        INSERT itam_ref.model(manufacturer_id,type_code,name_en) VALUES(1,'PC',N'Latitude'),(1,'MON',N'P24'),(2,'IP',N'8841');
        """);
    JsonElement E(object o) => JsonSerializer.SerializeToElement(o, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Check(await Throws<ItamAccessDenied>(() => store.Change(U(3), new("supplier", "save", E(new { name = "X", active = true }), null), default)), "Only the IT Manager changes master data");
    await store.Change(U(7), new("supplier", "save", E(new { name = "Supplier Two", vat = "300000000000003", active = true }), "check"), default);
    Check(await Throws<ArgumentException>(() => store.Change(U(7), new("supplier", "save", E(new { name = "Bad VAT", vat = "123", active = true }), null), default)), "Supplier VAT number must be 15 digits");
    Check(await Throws<ArgumentException>(() => store.Change(U(2), new("role", "save", E(new { employee = G(9), role = "SupportSpecialist" }), null), default)), "An IT Support Specialist needs a region");
    await store.Change(U(2), new("role", "save", E(new { employee = G(9), role = "SupportSpecialist", region = "Central" }), null), default);
    Check(await Throws<ArgumentException>(() => store.Change(U(2), new("role", "save", E(new { employee = G(9), role = "SupportSpecialist", region = "Central" }), null), default)), "The same role twice is refused");
    // Several roles per person (owner's structure): the IT Manager is also the System Administrator; rights add up.
    await store.Change(U(2), new("role", "save", E(new { employee = G(7), role = "SystemAdministrator" }), null), default);
    var me7 = JsonSerializer.SerializeToElement(await store.Me(U(7), default), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Check(me7.GetProperty("roles").GetArrayLength() == 2 && me7.GetProperty("can").GetProperty("masterData").GetBoolean() && me7.GetProperty("can").GetProperty("tasks").GetBoolean(),
        "IT Manager and System Administrator together: both sets of rights");
    Check(await Throws<ArgumentException>(() => store.Change(U(2), new("role", "save", E(new { employee = G(9), role = "InkOfficer" }), null), default)), "An Ink Officer needs a region");
    await store.Change(U(2), new("role", "save", E(new { employee = G(9), role = "InkOfficer", region = "Central" }), null), default);
    Check(await Throws<ItamAccessDenied>(() => store.Change(U(1), new("role", "save", E(new { employee = G(10), role = "TeamLeader" }), null), default)), "Only the IT Manager gives roles");
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
    Check(await Throws<ItamAccessDenied>(() => store.Register(U(8), A("MON", 2, 2, "MN-0102"), default)), "An employee without an IT role cannot register");
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
    Check(List(5).Contains("PC-4809") && List(5).Contains("MON-4810"), "System Administrator sees all assets");
    Check(List(6).Contains("IP-") && !List(6).Contains("PC-4809"), "Voice Administrator sees IP phones only");
    Check(List(3).Contains("PC-4809") && List(3).Contains("MON-4810") && List(7).Contains("MON-4810"), "Team Leader and IT Manager see all assets");
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
    Check(await Throws<ItamAccessDenied>(() => store.CreatePo(U(6), poInput, new("po.pdf", "application/pdf", poPdf), default)), "The Voice Administrator cannot record POs");
    Check(await Throws<ArgumentException>(() => store.CreatePo(U(1), poInput, null, default)) && await Throws<ArgumentException>(() => store.CreatePo(U(1), poInput, new("po.png", "image/png", [0x89, 0x50, 0x4E, 0x47, 1, 2]), default)),
        "The PO document (PDF) is required");
    var po = JsonSerializer.SerializeToElement(await store.CreatePo(U(1), poInput, new("po.pdf", "application/pdf", poPdf), default)).GetProperty("id").GetInt32();
    Check(await Throws<ArgumentException>(() => store.CreatePo(U(3), poInput, new("po.pdf", "application/pdf", poPdf), default)), "The same PO number twice is refused");
    Check(await Scalar($"SELECT request_ref FROM itam_data.po WHERE po_id={po}") as string == "PR-3", "Request number stored as PR-n");
    int Line(int no) => (int)Scalar($"SELECT po_line_id FROM itam_data.po_line WHERE po_id={po} AND line_no={no}").Result!;
    ItamDeviceInput Dev(string? serial, string? tag = null) => new(serial, tag, null, null, "New", new() { ["ComputerType"] = "Notebook", ["Processor"] = "Core i7", ["MemoryGb"] = "16", ["CapacityGb"] = "512", ["OperatingSystem"] = "Win11" }, null, null, null);
    var r1 = JsonSerializer.SerializeToElement(await store.Receive(U(1), po, new(Line(1), 1, null, "INV-1", null, [Dev("POSN1", "POTAG1"), Dev("POSN2", "POTAG2")]), null, default));
    Check(await Throws<ItamAccessDenied>(() => store.Receive(U(4), po, new(Line(1), 1, null, null, null, [Dev("POSN9", "POTAG9")]), null, default)), "Specialists record POs but receive devices into their regions only");
    Check(r1.GetProperty("assets").GetArrayLength() == 2, "Partial receipt: 2 of 3");
    var before = await Scalar("SELECT COUNT(*) FROM itam_data.asset");
    Check((await Error(() => store.Receive(U(1), po, new(Line(1), 1, null, null, null, [Dev("POSN3", "POTAG3"), Dev("POSN4", "POTAG4")]), null, default))).Contains("still to come")
        && Equals(await Scalar("SELECT COUNT(*) FROM itam_data.asset"), before), "Receiving more than the remaining quantity is refused; nothing saved");
    Check((await Error(() => store.Receive(U(1), po, new(Line(2), 1, null, null, null, [new("SAME1", null, null, null, "New", null, null, null, null), new("same-1", null, null, null, "New", null, null, null, null)]), null, default))).Contains("twice")
        && Equals(await Scalar("SELECT COUNT(*) FROM itam_data.asset"), before), "The same serial twice in one receipt is refused; nothing saved");
    Check((await Error(() => store.Receive(U(1), po, new(Line(1), 1, null, null, null, [Dev(null, "TAG1")]), null, default))).Contains("Device 1"), "A service tag already in IT Assets is refused, naming the row");
    Check(await Scalar("SELECT COUNT(*) FROM itam_data.asset WHERE po_id IS NOT NULL AND unit_cost=1000 AND supplier_id=1 AND status='InStore'") is 2, "Received devices carry the PO cost and supplier, In Store");
    Check((await Error(() => store.Register(U(1), A("MON", 1, 2, "MN-0500") with { PoNumber = "PO-77" }, default))).Contains("receive the device on the PO"), "Registering by hand against a recorded PO is refused");
    var view = JsonSerializer.SerializeToElement(await store.Po(U(5), po, default), new JsonSerializerOptions(JsonSerializerDefaults.Web));
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
    var pack = await store.PoPack(U(5), po, default);
    using (var ms = new MemoryStream(pack.Content)) using (var read = PdfReader.Open(ms, PdfDocumentOpenMode.Import))
        Check(read.PageCount == 2, "PO pack: report page + the PO document page");
    Check(await Scalar($"SELECT COUNT(*) FROM itam_data.pack_print WHERE po_id={po}") is 1, "Each print is recorded");
    var print = (int)(await Scalar($"SELECT print_id FROM itam_data.pack_print WHERE po_id={po}"))!;
    Check(await Throws<ItamAccessDenied>(() => store.MarkSent(U(6), po, print, null, default)), "The Voice Administrator cannot record the sending to Accounting");
    await store.MarkSent(U(1), po, print, null, default);
    Check(await Scalar($"SELECT sent_on FROM itam_data.pack_print WHERE print_id={print}") is not null, "Sent to Accounting recorded");
    Check(await Throws<ItamAccessDenied>(() => store.PoPack(U(6), po, default)), "The Voice Administrator cannot print PO documents");
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
        "Specialists deliver in their regions only; an employee without an IT role cannot deliver");
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
    var note = await store.DeliveryNote(U(5), dn1Id, default);
    Check(System.Text.Encoding.ASCII.GetString(note.Content, 0, 5) == "%PDF-", "Delivery note PDF (the System Administrator can read it)");
    var scanPage = pdf.Start(); scanPage.Text("signed", 40, scanPage.Y, 12); var scan = new ItamUpload("signed.pdf", "application/pdf", scanPage.Build());
    Check(await Throws<ItamAccessDenied>(() => store.UploadSigned(U(8), dn1Id, scan, default)), "An employee without an IT role cannot upload signed copies");
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
    Check(await Throws<ItamAccessDenied>(() => store.TaskDone(U(6), adTask, new(null), default)), "The Voice Administrator cannot confirm the AD and Endpoint Central removal");
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
    Check(await Throws<ItamAccessDenied>(() => store.TaskDone(U(3), cucm, new(null), default)), "The Team Leader cannot confirm the CUCM removal (System Administrator and Voice Administrator do)");
    await store.TaskDone(U(6), cucm, new(null), default);
    var sysTasks = JsonSerializer.SerializeToElement(await store.MyTasks(U(5), default)).GetProperty("tasks").EnumerateArray().ToList();
    var voiceTasks = JsonSerializer.SerializeToElement(await store.MyTasks(U(6), default)).GetProperty("tasks").EnumerateArray().ToList();
    Check(sysTasks.Any(t => t.GetProperty("kind").GetString() == "RemoveFromAd") && sysTasks.Any(t => t.GetProperty("kind").GetString() == "RemoveFromCucm") && voiceTasks.Count > 0 && voiceTasks.All(t => t.GetProperty("kind").GetString() == "RemoveFromCucm")
        && await Throws<ItamAccessDenied>(() => store.MyTasks(U(4), default)), "Tasks page: the System Administrator sees both removals, the Voice Administrator extension removals");
    Check(await Throws<ItamAccessDenied>(() => store.LabelPdf(U(4), hpc2, default)) && await Throws<ItamAccessDenied>(() => store.LabelPdf(U(8), hpc2, default)), "Labels: own regions for specialists; not employees without an IT role");
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
    Check(await Throws<ItamAccessDenied>(() => store.SendInkForApproval(U(4), ink1, new(G(12)), default))
        && await Throws<ArgumentException>(() => store.SendInkForApproval(U(9), ink1, new(G(10)), default))
        && await Throws<ArgumentException>(() => store.SendInkForApproval(U(9), ink1, new(G(11)), default)), "A specialist cannot send ink for approval; the approver is an active employee other than the requester");
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
        && await Throws<ItamAccessDenied>(() => store.InkRequestPdf(U(6), ink1, default)), "The employee and the approver read the ink request document; the Voice Administrator cannot");
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
    // Maintenance orders (form 07): the IT Assets Officer creates with the quotation; the IT Manager (never the creator)
    // approves; dispatch puts the asset In Repair; the return closes it with the outcome and invoice. IT repairs on the history.
    var pnt = (string)(await Scalar($"SELECT asset_no FROM itam_data.asset WHERE asset_id={printer}"))!;
    var qDoc = pdf.Start(); qDoc.Text("quotation", 40, qDoc.Y, 12); var qFile = new ItamUpload("q.pdf", "application/pdf", qDoc.Build());
    Check(await Throws<ItamAccessDenied>(() => store.CreateMaintenanceOrder(U(6), new(pnt, 1, "Q1", 100m, "Jam", DateOnly.FromDateTime(DateTime.Today).AddDays(3)), qFile, default))
        && await Throws<ArgumentException>(() => store.CreateMaintenanceOrder(U(1), new(pnt, 1, "Q1", 100m, "Jam", DateOnly.FromDateTime(DateTime.Today).AddDays(3)), null, default)), "The Voice Administrator cannot create orders; the quotation file is required");
    var mo = JsonSerializer.SerializeToElement(await store.CreateMaintenanceOrder(U(1), new(pnt, 1, "Q1", 100m, "Paper jam: replace the fuser", DateOnly.FromDateTime(DateTime.Today).AddDays(3)), qFile, default)).GetProperty("id").GetInt32();
    Check(await Throws<ArgumentException>(() => store.DispatchMaintenance(U(1), mo, new(null), default)) && await Throws<ItamAccessDenied>(() => store.DecideMaintenance(U(9), mo, new(true, null), default))
        && await Throws<ArgumentException>(() => store.DecideMaintenance(U(2), mo, new(false, null), default))
        && await Throws<ArgumentException>(() => store.CreateMaintenanceOrder(U(1), new(pnt, 1, "Q2", 1m, "x", DateOnly.FromDateTime(DateTime.Today).AddDays(3)), qFile, default)),
        "Not sent before approval; only the IT Manager decides; a rejection needs a reason; one open order per asset");
    await store.DecideMaintenance(U(2), mo, new(true, null), default);
    await store.DispatchMaintenance(U(1), mo, new(null), default);
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={printer}") as string == "InRepair", "Dispatched: the asset is In Repair");
    Check(await Throws<ArgumentException>(() => store.ReturnMaintenance(U(1), mo, new(null, null, "Repaired", "Fuser replaced", 100m, null), default)), "A repaired order needs the invoice number");
    await store.ReturnMaintenance(U(1), mo, new(null, null, "Repaired", "Fuser replaced", 120m, "INV-P1"), default);
    var hist = JsonSerializer.SerializeToElement(await store.Asset(U(1), printer, default)).GetProperty("record");
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={printer}") as string == "InUse"
        && await Scalar($"SELECT CASE WHEN invoice_id IS NOT NULL AND final_cost=120 THEN 1 ELSE 0 END FROM itam_data.maintenance_order WHERE order_id={mo}") is 1
        && hist.GetProperty("orders").GetArrayLength() == 1, "Back with its holder (In Use); the invoice number links the recorded invoice; the history lists the order");
    await store.ItRepair(U(9), printer, new("Cleaned the rollers"), default);
    Check(JsonSerializer.SerializeToElement(await store.Asset(U(1), printer, default)).GetProperty("record").GetProperty("itRepairs").GetArrayLength() == 1
        && await Throws<ItamAccessDenied>(() => store.ItRepair(U(10), printer, new("x"), default)), "Repairs by IT are recorded on the history by IT");
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
    Check(await Throws<ItamAccessDenied>(() => store.RegisterInkPrinter(U(6), newInk, new(1, 1, null, "Brother HL-L2350", "SN-NEW-1", "T1", null, "Used"), default)), "The Voice Administrator cannot register the printer (IT handlers and specialists can)");
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
    // ---- Device and accessory requests (step 3) and My IT Assets ------------------------------------------------------------------
    await Exec($"UPDATE itam_access.employee SET manager_id='{G(12)}',department=N'Sales',region=N'Central' WHERE employee_id='{G(10)}'");
    var rToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
    Check(await Throws<ItamAccessDenied>(() => store.CreateRequest(U(9), new(G(10), "New", null, "x"), null, default))
        && await Throws<ArgumentException>(() => store.CreateRequest(U(10), new(null, "Replacement", null, "x"), null, default)), "Ask for yourself or your team; a replacement names the device in custody");
    var pr1 = JsonSerializer.SerializeToElement(await store.CreateRequest(U(10), new(null, "New", null, "A second monitor for reports"), null, default)).GetProperty("id").GetInt32();
    Check(await Scalar($"SELECT request_no+'|'+status+'|'+region_code FROM itam_data.request WHERE request_id={pr1}") as string == "PR-1|WaitingRequestApproval|Central"
        && await Throws<ItamAccessDenied>(() => store.DecideRequest(U(10), pr1, new(true, null), default)) && await Throws<ItamAccessDenied>(() => store.DecideRequest(U(1), pr1, new(true, null), default)),
        "PR-1 waits for the direct manager; the employee and IT cannot approve it");
    await store.DecideRequest(U(12), pr1, new(true, null), default);
    var rMon = JsonSerializer.SerializeToElement(await store.Register(U(1), A("MON", 1, 2, "SN-MON-R1"), default)).GetProperty("id").GetInt32();
    Check(await Throws<ItamAccessDenied>(() => store.ReviewRequest(U(9), pr1, new("Stock", rMon, G(9), null), default)), "Only the IT Assets Officer chooses the source");
    await store.ReviewRequest(U(1), pr1, new("Stock", rMon, G(9), null), default);
    var rDl = JsonSerializer.SerializeToElement(await store.Deliver(U(9), rMon, Dl(G(10), null), default)).GetProperty("id").GetInt32();
    await store.FulfilRequest(U(9), pr1, new(rDl), default);
    Check(await Scalar($"SELECT status FROM itam_data.request WHERE request_id={pr1}") as string == "Fulfilled", "From stock: reserved, delivered by the assigned specialist, fulfilled by its delivery note");
    Check(await Throws<ArgumentException>(() => store.CreateRequest(U(10), new(null, "Replacement", rMon, "The screen flickers"), null, default)), "A replacement needs the technical assessment when none is recorded");
    var pr2 = JsonSerializer.SerializeToElement(await store.CreateRequest(U(10), new(null, "Replacement", rMon, "The screen flickers"), qFile, default)).GetProperty("id").GetInt32();
    await store.DecideRequest(U(12), pr2, new(true, null), default);
    await store.ReviewRequest(U(1), pr2, new("NewDevice", null, null, null), default);
    Check(await Throws<ArgumentException>(() => store.SubmitCost(U(1), pr2, new(1, null, [new("Monitor 24", "IPS", 1, 600m)]), null, default)), "A new device needs the supplier's quotation");
    await store.SubmitCost(U(1), pr2, new(1, null, [new("Monitor 24", "IPS", 1, 600m), new("Cable", null, 2, 25m)]), qFile, default);
    Check(await Throws<ItamAccessDenied>(() => store.DecideCost(U(1), pr2, new(true, null), default)), "The cost is approved by the direct manager or the IT Manager, not other IT");
    await store.DecideCost(U(12), pr2, new(true, null), default);
    var prAd = E(await store.Request(U(12), pr2, default)).GetProperty("employeeAd");
    Check(prAd.GetProperty("account").GetString() == E(await store.Request(U(10), pr2, default)).GetProperty("employeeAd").GetProperty("account").GetString() && prAd.GetProperty("department").GetString() == "Sales"
        , "A device request shows the requester's Active Directory details");
    var prDoc = await store.RequestPdf(U(1), pr2, default);
    Check(System.Text.Encoding.ASCII.GetString(prDoc.Content, 0, 5) == "%PDF-" && await Scalar($"SELECT status FROM itam_data.request WHERE request_id={pr2}") as string == "Approved"
        && ItamStore.AmountInWords(747.50m) == "Seven hundred forty-seven riyals and fifty halalas only", "Approved cost; request document with the amount in words (650 + VAT = 747.50)");
    await store.MarkOrdered(U(1), pr2, new(null), default); await store.RequestAccounting(U(1), pr2, new(null), default);
    var pr3 = JsonSerializer.SerializeToElement(await store.CreateRequest(U(10), new(null, "New", null, "A wireless mouse"), null, default)).GetProperty("id").GetInt32();
    Check(await Throws<ItamAccessDenied>(() => store.DecideRequest(U(5), pr3, new(true, null), default)), "The System Administrator does not approve device requests");
    await store.DecideRequest(U(2), pr3, new(true, null), default);
    await store.ReviewRequest(U(1), pr3, new("Accessory", null, G(9), null), default);
    await store.SubmitCost(U(9), pr3, new(null, "Local shop", [new("Wireless mouse", null, 1, 100m)]), null, default);
    await store.DecideCost(U(2), pr3, new(true, null), default);
    var rcpt = new ItamUpload("receipt.pdf", "application/pdf", qFile.Content);
    Check(await Scalar($"SELECT status FROM itam_data.request WHERE request_id={pr3}") as string == "CashPurchase"
        && await Throws<ArgumentException>(() => store.RecordCashPurchase(U(9), pr3, new("Local shop", rToday, 116m), rcpt, default)), "Accessory approved by the IT Manager (request and cost): priced in the region, bought for no more than the approved total (115)");
    await store.RecordCashPurchase(U(9), pr3, new("Local shop", rToday, 115m), rcpt, default);
    var my = JsonSerializer.SerializeToElement(await store.MyItAssets(U(10), default));
    var boss = JsonSerializer.SerializeToElement(await store.MyItAssets(U(12), default));
    Check(my.GetProperty("assets").GetArrayLength() >= 1 && my.GetProperty("requests").GetArrayLength() == 3 && boss.GetProperty("team").EnumerateArray().Any(t => t.GetProperty("name").GetString() == "Person 10")
        && System.Text.Encoding.ASCII.GetString((await store.DeliveryNote(U(10), rDl, default)).Content, 0, 5) == "%PDF-" && await Throws<ItamAccessDenied>(() => store.DeliveryNote(U(4), rDl, default)),
        "My IT Assets: my assets, requests and delivery notes; the manager sees the team; others cannot read the note");
    // Approval deadline: not decided by the manager within 3 days (request or cost approval) -> cancelled automatically.
    var late = JsonSerializer.SerializeToElement(await store.CreateRequest(U(10), new(null, "New", null, "A headset"), null, default)).GetProperty("id").GetInt32();
    var fresh = JsonSerializer.SerializeToElement(await store.CreateRequest(U(10), new(null, "New", null, "A webcam"), null, default)).GetProperty("id").GetInt32();
    var lateCost = JsonSerializer.SerializeToElement(await store.CreateRequest(U(10), new(null, "New", null, "A dock"), null, default)).GetProperty("id").GetInt32();
    await store.DecideRequest(U(12), lateCost, new(true, null), default); await store.ReviewRequest(U(1), lateCost, new("NewDevice", null, null, null), default);
    await store.SubmitCost(U(1), lateCost, new(1, null, [new("Dock", null, 1, 300m)]), qFile, default);
    await Exec($"UPDATE itam_data.request SET requested_at_utc=DATEADD(hour,-73,SYSUTCDATETIME()) WHERE request_id={late}; UPDATE itam_data.request SET requested_at_utc=DATEADD(hour,-47,SYSUTCDATETIME()) WHERE request_id={fresh}; " +
        $"UPDATE itam_data.request SET priced_at_utc=DATEADD(hour,-73,SYSUTCDATETIME()) WHERE request_id={lateCost}");
    Check(await store.ExpireRequests(default) == 2 && await Scalar($"SELECT status FROM itam_data.request WHERE request_id={late}") as string == "Cancelled"
        && await Scalar($"SELECT status FROM itam_data.request WHERE request_id={lateCost}") as string == "Cancelled" && await Scalar($"SELECT status FROM itam_data.request WHERE request_id={fresh}") as string == "WaitingRequestApproval"
        && await Throws<ArgumentException>(() => store.DecideRequest(U(12), late, new(true, null), default)), "Not decided within 3 days (request or cost): cancelled automatically; a 2-day-old request still waits");
    await Exec($"UPDATE itam_data.request SET requested_at_utc=DATEADD(hour,-80,SYSUTCDATETIME()) WHERE request_id={fresh}");
    Check(await Throws<ArgumentException>(() => store.DecideRequest(U(12), fresh, new(true, null), default)) && await Scalar($"SELECT status FROM itam_data.request WHERE request_id={fresh}") as string == "Cancelled",
        "A late decision is refused: the request is cancelled first, even between timer runs");
    // ---- Stock movements (step 4): branch transfer gate pass, warehouse transfer, exit pass ----------------------------------------
    var mvDay = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
    Check(await Throws<ArgumentException>(() => store.CreateMovement(U(10), new("Branch", null, [rMon], "Move to another branch", null, null, 1, 1, null, mvDay, mvDay.AddDays(3), "Employee", null, null, null, G(12), null), default)),
        "A branch transfer goes to another branch");
    var gp = JsonSerializer.SerializeToElement(await store.CreateMovement(U(10), new("Branch", null, [rMon], "Move to another branch", null, null, 1, 2, null, mvDay, mvDay.AddDays(3), "Employee", null, null, null, G(12), null), default));
    var gpId = gp.GetProperty("id").GetInt32();
    Check(E(await store.Movement(U(3), gpId, default)).GetProperty("employeeAd").GetProperty("enabled").GetBoolean(), "A gate pass shows the requester's Active Directory details");
    Check(gp.GetProperty("movementNo").GetString()!.StartsWith("GP-") && await Throws<ArgumentException>(() => store.GatePassPdf(U(10), gpId, default))
        && await Throws<ArgumentException>(() => store.CreateMovement(U(10), new("Exit", null, [rMon], "Work from home", null, "Returnable", 1, null, "Home", mvDay, mvDay.AddDays(2), "Employee", null, null, null, null, null), default)),
        "Gate pass GP-n for the employee's own asset; no pass before approval; an asset is on one movement at a time");
    Check(await Throws<ItamAccessDenied>(() => store.DecideMovement(U(10), gpId, new(true, null, null), default)) && await Throws<ArgumentException>(() => store.DispatchMovement(U(10), gpId, new(null), default)),
        "Nobody approves their own pass; no dispatch before approval");
    await store.DecideMovement(U(3), gpId, new(true, null, null), default);
    Check(System.Text.Encoding.ASCII.GetString((await store.GatePassPdf(U(10), gpId, default)).Content, 0, 5) == "%PDF-" && await Throws<ItamAccessDenied>(() => store.DispatchMovement(U(12), gpId, new(null), default)),
        "Approved by the Team Leader: the gate pass prints; only the named sender or IT dispatches");
    await store.DispatchMovement(U(10), gpId, new(null), default);
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={rMon}") as string == "InTransit" && await Throws<ItamAccessDenied>(() => store.ReceiveMovement(U(4), gpId, new([new(0, "Received", null)]), default)),
        "Dispatched: In Transit; only the named receiver confirms");
    var gpLine = (int)(await Scalar($"SELECT line_id FROM itam_data.movement_line WHERE movement_id={gpId}"))!;
    var oldDl = (int)(await Scalar($"SELECT delivery_id FROM itam_data.delivery WHERE asset_id={rMon} AND received_at_utc IS NULL"))!;
    await store.ReceiveMovement(U(12), gpId, new([new(gpLine, "Received", null)]), default);
    Check(await Scalar($"SELECT new_delivery_id FROM itam_data.movement_line WHERE line_id={gpLine}") is null && await Scalar($"SELECT CAST(location_id AS varchar(5))+status FROM itam_data.asset WHERE asset_id={rMon}") as string == "2InStore"
        && await Scalar($"SELECT closure_reason FROM itam_data.delivery WHERE delivery_id={oldDl}") as string == "Transfer" && await Throws<ArgumentException>(() => store.ReceiptNote(U(1), oldDl, default))
        && await Scalar($"SELECT status FROM itam_data.movement WHERE movement_id={gpId}") as string == "Completed",
        "Received by another employee: the old custody closes on the transfer (no receipt note) and the asset is In Store at the destination");
    var newDl = JsonSerializer.SerializeToElement(await store.Deliver(U(4), rMon, Dl(G(12), null), default)).GetProperty("id").GetInt32();
    Check(await Scalar($"SELECT employee_id FROM itam_data.delivery WHERE delivery_id={newDl}") is Guid nd && nd == G(12), "IT of the destination delivers it to the receiver from In Store, with a new delivery note");
    // Warehouse transfer: no approval; partial dispatch; cancel the rest; a missing asset resolved by the Team Leader with evidence.
    var w1 = JsonSerializer.SerializeToElement(await store.Register(U(1), A("MON", 1, 2, "WT-MON-1"), default)).GetProperty("id").GetInt32();
    var w2 = JsonSerializer.SerializeToElement(await store.Register(U(1), A("MON", 1, 2, "WT-MON-2"), default)).GetProperty("id").GetInt32();
    var w3 = JsonSerializer.SerializeToElement(await store.Register(U(1), A("MON", 1, 2, "WT-MON-3"), default)).GetProperty("id").GetInt32();
    ItamMovementInput Wt(int[] ids, Guid receiver) => new("Warehouse", null, ids, "Stock for the West", null, null, 1, 2, null, mvDay, mvDay.AddDays(4), "Other", "Demo Driver", "0555555555", null, receiver, null);
    Check(await Throws<ItamAccessDenied>(() => store.CreateMovement(U(4), Wt([w1], G(4)), default)) && await Throws<ArgumentException>(() => store.CreateMovement(U(9), Wt([w1], G(10)), default)),
        "Only IT of the source region creates a warehouse transfer; the receiver is IT of the destination region");
    Check(await Throws<ArgumentException>(() => store.CreateMovement(U(9), Wt([.. Enumerable.Range(1, 201)], G(4)), default)), "Several assets on one transfer, at most 200");
    var wt = JsonSerializer.SerializeToElement(await store.CreateMovement(U(9), Wt([w1, w2], G(4)), default)); var wtId = wt.GetProperty("id").GetInt32();
    var wl0 = (int)(await Scalar($"SELECT MIN(line_id) FROM itam_data.movement_line WHERE movement_id={wtId}"))!;
    await store.DispatchMovement(U(9), wtId, new([wl0]), default);
    Check(wt.GetProperty("movementNo").GetString()!.StartsWith("WT-") && await Scalar($"SELECT status FROM itam_data.movement WHERE movement_id={wtId}") as string == "PartiallyDispatched"
        && await Throws<ArgumentException>(() => store.CreateMovement(U(9), Wt([w2], G(4)), default)), "WT-n with no approval; partial dispatch keeps the rest reserved");
    await store.CancelMovement(U(9), wtId, new("Second one stays"), default);
    await store.ReceiveMovement(U(4), wtId, new([new(wl0, "Missing", "Not on the truck")]), default);
    Check(await Scalar($"SELECT status FROM itam_data.movement WHERE movement_id={wtId}") as string == "ReceiptDisputed" && await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={w2}") as string == "InStore"
        && await Throws<ItamAccessDenied>(() => store.ResolveMovement(U(9), wtId, new(wl0, "ConfirmedLost", "x"), scan, default)), "Missing asset: discrepancy pending; the cancelled one is free again; only an approver resolves");
    await store.ResolveMovement(U(3), wtId, new(wl0, "ConfirmedLost", "The driver confirmed it fell off"), scan, default);
    Check(await Scalar($"SELECT status FROM itam_data.movement WHERE movement_id={wtId}") as string == "ClosedWithExceptions" && await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={w1}") as string == "Lost",
        "Confirmed lost: the transfer closes with exceptions; the asset is Lost, never received");
    var wt2 = JsonSerializer.SerializeToElement(await store.CreateMovement(U(9), Wt([w3], G(4)), default)).GetProperty("id").GetInt32();
    await store.DispatchMovement(U(9), wt2, new(null), default);
    var wl2 = (int)(await Scalar($"SELECT line_id FROM itam_data.movement_line WHERE movement_id={wt2}"))!;
    await store.ReceiveMovement(U(4), wt2, new([new(wl2, "Received", null)]), default);
    Check(await Scalar($"SELECT status FROM itam_data.movement WHERE movement_id={wt2}") as string == "Completed" && await Scalar($"SELECT CAST(location_id AS varchar(5))+status FROM itam_data.asset WHERE asset_id={w3}") as string == "2InStore"
        && await Scalar($"SELECT COUNT(*) FROM itam_data.delivery WHERE asset_id={w3}") is 0, "Received: In Store at the destination warehouse, with no delivery note");
    // Exit pass, returnable; and an approved pass not dispatched in time expires.
    var exitAsset = JsonSerializer.SerializeToElement(await store.Register(U(1), A("MON", 1, 2, "EXIT-MON-1"), default)).GetProperty("id").GetInt32();
    await store.Deliver(U(1), exitAsset, Dl(G(10), null), default);
    var ex = JsonSerializer.SerializeToElement(await store.CreateMovement(U(10), new("Exit", null, [exitAsset], "Work from home", null, "Returnable", 1, null, "Home", mvDay, mvDay.AddDays(2), "Employee", null, null, null, null, null), default)).GetProperty("id").GetInt32();
    Check(await Throws<ItamAccessDenied>(() => store.DecideMovement(U(6), ex, new(true, null, null), default)), "The Voice Administrator does not approve gate passes");
    await store.DecideMovement(U(5), ex, new(true, null, null), default); await store.DispatchMovement(U(10), ex, new(null), default);
    Check(await Scalar($"SELECT status FROM itam_data.movement WHERE movement_id={ex}") as string == "Out", "Approved by the System Administrator: the exit pass is out of the building until the return");
    await store.ReturnMovement(U(10), ex, default);
    var ex2 = JsonSerializer.SerializeToElement(await store.CreateMovement(U(10), new("Exit", null, [exitAsset], "Work from home", null, "Returnable", 1, null, "Home", mvDay, mvDay.AddDays(2), "Employee", null, null, null, null, null), default)).GetProperty("id").GetInt32();
    await store.DecideMovement(U(2), ex2, new(true, null, null), default);
    await Exec($"UPDATE itam_data.movement SET exit_date=DATEADD(day,-5,exit_date),valid_until=DATEADD(day,-1,CAST(SYSUTCDATETIME() AS date)) WHERE movement_id={ex2}");
    Check(await Scalar($"SELECT status FROM itam_data.movement WHERE movement_id={ex}") as string == "Completed" && await store.ExpireMovements(default) == 1
        && await Scalar($"SELECT status FROM itam_data.movement WHERE movement_id={ex2}") as string == "Expired" && await Throws<ArgumentException>(() => store.DispatchMovement(U(10), ex2, new(null), default)),
        "Return confirmed closes the pass; an approved pass not dispatched within its validity expires and cannot be used");
    Console.WriteLine("PASS ink and invoices");

    // ---- End of life (step 5): loss, disposal, sale, leavers, AD comparison, licenses ----------------------------------------------
    var eDay = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
    int NewId(object o) => JsonSerializer.SerializeToElement(o).GetProperty("id").GetInt32();
    async Task<string> NoOf(int assetId) => (string)(await Scalar($"SELECT asset_no FROM itam_data.asset WHERE asset_id={assetId}"))!;
    // Loss: reported by the holder; never a receipt; the decision (here a settlement) clears the employee, the write-off does not.
    var lpc = NewId(await store.Register(U(1), A("PC", 1, 1, null, "TAG-LOSS", null, pcAttrs), default));
    var lDl = NewId(await store.Deliver(U(1), lpc, Dl(G(10), "ATWK-L01"), default));
    var lpcNo = await NoOf(lpc);
    Check(await Throws<ItamAccessDenied>(() => store.ReportLoss(U(12), new(lpcNo, eDay, "Office", "Gone"), null, default)), "Only the holder or IT of the region reports a loss");
    var lc = NewId(await store.ReportLoss(U(10), new(lpcNo, eDay, "Abha office", "Not found after the weekend"), null, default));
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={lpc}") as string == "Lost" && await Scalar($"SELECT received_at_utc FROM itam_data.delivery WHERE delivery_id={lDl}") is null
        && await Throws<ArgumentException>(() => store.ReturnAsset(U(1), lDl, new(null, "Working", 1, null), default)) && await Throws<ArgumentException>(() => store.ReportLoss(U(1), new(lpcNo, eDay, "x", "y"), null, default)),
        "Loss LC-n: the asset is Lost, the custody stays open, no receipt; one open case per asset");
    Check(await Throws<ItamAccessDenied>(() => store.AssignLoss(U(3), lc, new(G(3)), default)) && await Throws<ArgumentException>(() => store.AssignLoss(U(2), lc, new(G(10)), default))
        && await Throws<ArgumentException>(() => store.DecideLoss(U(2), lc, new("NotLiable", "x", null), scan, default)), "Only the IT Manager assigns; never the holder; no decision before the findings");
    await store.AssignLoss(U(2), lc, new(G(3)), default);
    Check(await Throws<ItamAccessDenied>(() => store.LossFindings(U(4), lc, new("f", "r"), null, default)), "Only the investigator records the findings");
    await store.LossFindings(U(3), lc, new("Left in a taxi.", "Settlement of the depreciated value"), null, default);
    Check(await Throws<ItamAccessDenied>(() => store.DecideLoss(U(3), lc, new("NotLiable", "x", null), scan, default)) && await Throws<ArgumentException>(() => store.DecideLoss(U(2), lc, new("Settlement", "x", null), null, default)),
        "The IT Manager decides with the signed decision; the investigator does not");
    await store.DecideLoss(U(2), lc, new("Settlement", "Pays the depreciated value", 500m), scan, default);
    await store.RequestWriteOff(U(1), lc, new("Remaining book value"), default);
    Check(await Throws<ItamAccessDenied>(() => store.DecideWriteOff(U(1), lc, new(true, null), default)), "The person who asked does not approve the write-off");
    await store.DecideWriteOff(U(2), lc, new(true, null), default);
    Check(await Scalar($"SELECT responsibility+N'|'+writeoff+N'|'+status FROM itam_data.loss_case WHERE case_id={lc}") as string == "PendingRequiredAction|Approved|AwaitingAction"
        && await Scalar($"SELECT received_at_utc FROM itam_data.delivery WHERE delivery_id={lDl}") is null, "An approved write-off does not clear the employee; the custody stays open until the settlement");
    await store.SettleLoss(U(1), lc, new("Paid 500 SAR to Accounting"), scan, default);
    Check(await Scalar($"SELECT responsibility+N'|'+status FROM itam_data.loss_case WHERE case_id={lc}") as string == "Cleared|ClosedByDecision"
        && await Scalar($"SELECT closure_reason FROM itam_data.delivery WHERE delivery_id={lDl}") as string == "LossDecision" && await Throws<ArgumentException>(() => store.ReceiptNote(U(1), lDl, default)),
        "The settlement clears the employee and closes the custody by the loss decision, with no receipt note");
    Check(await Throws<ItamAccessDenied>(() => store.RecoverLoss(U(4), lc, new(eDay, 1, "Working", "Found"), default)), "IT receives a recovered asset into their regions only");
    await store.RecoverLoss(U(1), lc, new(eDay, 1, "Working", "Returned by the taxi company"), default);
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={lpc}") as string == "Lost", "Recovered: still Lost until the review");
    await store.ReviewRecovery(U(1), lc, new("Working; the settlement stays"), default);
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={lpc}") as string == "InStore", "Reviewed: back In Store");

    // Disposal: approval alone never disposes; completion needs evidence; computers need data wiping and license removal.
    var dmon = NewId(await store.Register(U(1), A("MON", 1, 2, "DISP-MON-1"), default));
    var dmonNo = await NoOf(dmon);
    Check(await Throws<ItamAccessDenied>(() => store.RequestDisposal(U(4), new(dmonNo, "Broken", "Cracked"), default)), "Only IT of the asset's region asks for a disposal");
    var ds = NewId(await store.RequestDisposal(U(1), new(dmonNo, "Broken", "Cracked"), default));
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={dmon}") as string == "PendingDisposal" && await Throws<ArgumentException>(() => store.Deliver(U(1), dmon, Dl(G(10), null), default))
        && await Throws<ArgumentException>(() => store.CompleteDisposal(U(1), ds, new(eDay, "Recycling", "Ali", "E-waste Co.", null, null, null), scan, default)),
        "Disposal DS-n: Pending disposal approval blocks delivery; no completion before approval");
    Check(await Throws<ItamAccessDenied>(() => store.DecideDisposal(U(3), ds, new(true, null), default)), "Only the IT Manager approves a disposal");
    await store.DecideDisposal(U(2), ds, new(true, null), default);
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={dmon}") as string == "PendingDisposal" && await Throws<ItamAccessDenied>(() => store.CompleteDisposal(U(4), ds, new(eDay, "Recycling", "Ali", "E-waste Co.", null, null, null), scan, default))
        && await Throws<ArgumentException>(() => store.CompleteDisposal(U(1), ds, new(eDay, "Recycling", "Ali", "E-waste Co.", null, null, null), null, default)), "Approved: still Pending disposal approval; completion by the IT Assets Officer, with evidence");
    await store.CompleteDisposal(U(1), ds, new(eDay, "Recycling", "Ali", "E-waste Co.", null, null, null), scan, default);
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={dmon}") as string == "Disposed", "Completed with evidence: Disposed");
    var dpc = NewId(await store.Register(U(1), A("PC", 1, 1, null, "TAG-DISP", null, pcAttrs), default));
    var ds2 = NewId(await store.RequestDisposal(U(1), new(await NoOf(dpc), "Old", "Slow"), default));
    await store.DecideDisposal(U(7), ds2, new(true, null), default);
    Check(await Throws<ArgumentException>(() => store.CompleteDisposal(U(1), ds2, new(eDay, "Destruction", "Ali", null, false, true, null), scan, default)), "A computer is disposed only with its data wiped and licenses removed");
    await store.CompleteDisposal(U(1), ds2, new(eDay, "Destruction", "Ali", null, true, true, null), scan, default);
    var dmon2 = NewId(await store.Register(U(1), A("MON", 1, 2, "DISP-MON-2"), default));
    var ds3 = NewId(await store.RequestDisposal(U(1), new(await NoOf(dmon2), "Old", "Scratched"), default));
    Check(await Throws<ArgumentException>(() => store.DecideDisposal(U(2), ds3, new(false, null), default)), "A rejection needs a reason");
    await store.DecideDisposal(U(2), ds3, new(false, "Still usable"), default);
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={dmon2}") as string == "InStore" && await Scalar("SELECT COUNT(*) FROM itam_data.disposal WHERE assessment_id IS NOT NULL") is >= 1,
        "Rejected: back In Store; a Dispose assessment opened its own disposal request");

    // Sale: any IT role, proof of payment required, only the holder buys; the custody closes with the sale; AD or CUCM removal.
    var sip = NewId(await store.Register(U(1), A("IP", 1, 3, null, null, "00:AA:BB:CC:DD:01", new() { ["Extension"] = "4999" }, mf: 2), default));
    var sDl = NewId(await store.Deliver(U(1), sip, Dl(G(10), null), default));
    var sipNo = await NoOf(sip);
    Check(await Throws<ArgumentException>(() => store.RecordSale(U(6), new(sipNo, G(10), 50m, eDay, null), null, default))
        && await Throws<ArgumentException>(() => store.RecordSale(U(6), new(sipNo, G(12), 50m, eDay, null), scan, default))
        && await Throws<ItamAccessDenied>(() => store.RecordSale(U(10), new(sipNo, G(10), 50m, eDay, null), scan, default)), "A sale needs the proof of payment, the holder as buyer, and an IT role");
    await store.RecordSale(U(6), new(sipNo, G(10), 50m, eDay, "Old phone"), scan, default);
    Check(await Scalar($"SELECT status FROM itam_data.asset WHERE asset_id={sip}") as string == "Sold" && await Scalar($"SELECT closure_reason FROM itam_data.delivery WHERE delivery_id={sDl}") as string == "Sale"
        && await Scalar($"SELECT COUNT(*) FROM itam_data.task WHERE asset_id={sip} AND kind=N'RemoveFromCucm' AND done_at_utc IS NULL") is 1, "Sold to the holder: custody closed by the sale (no receipt note), CUCM removal task");
    var spc = NewId(await store.Register(U(1), A("PC", 1, 1, null, "TAG-SALE", null, pcAttrs), default));
    await store.RecordSale(U(1), new(await NoOf(spc), G(12), 700m, eDay, null), scan, default);
    Check(await Scalar($"SELECT COUNT(*) FROM itam_data.task WHERE asset_id={spc} AND kind=N'RemoveFromAd' AND delivery_id IS NULL") is 1
        && JsonSerializer.SerializeToElement(await store.Sales(U(4), default)).GetProperty("sales").GetArrayLength() == 2, "Sold from stock: AD removal task; both sales in the sales list");

    // Leavers: Resigned by the System Administrator; collect; clearance only when nothing is left; then disable the AD user.
    var lmon = NewId(await store.Register(U(1), A("MON", 1, 2, "LEAVE-MON-1"), default));
    var lvDl = NewId(await store.Deliver(U(1), lmon, Dl(G(12), null), default));
    Check(await Throws<ItamAccessDenied>(() => store.MarkResigned(U(1), new(G(12), eDay.AddDays(3), null), default)), "Only the System Administrator marks an employee Resigned");
    var cl = NewId(await store.MarkResigned(U(5), new(G(12), eDay.AddDays(3), null), default));
    var lv = JsonSerializer.SerializeToElement(await store.Leavers(U(1), default));
    var lvOther = NewId(await store.Register(U(1), A("MON", 1, 2, "LEAVE-MON-2"), default));
    Check(await Scalar($"SELECT owner_type FROM itam_data.delivery WHERE delivery_id={lvDl}") as string == "Resigned" && lv.GetProperty("collect").EnumerateArray().Any(x => x.GetProperty("deliveryId").GetInt32() == lvDl)
        && !JsonSerializer.SerializeToElement(await store.Leavers(U(4), default)).GetProperty("collect").EnumerateArray().Any(x => x.GetProperty("deliveryId").GetInt32() == lvDl)
        && await Throws<ArgumentException>(() => store.Deliver(U(1), lvOther, Dl(G(12), null), default)), "Resigned: on Assets to collect (in the region's list only); no new deliveries to a leaver");
    Check(await Throws<ArgumentException>(() => store.CompleteClearance(U(5), cl, new("HR form"), default)) && await Throws<ArgumentException>(() => store.UserDisabled(U(5), cl, new(null), default)),
        "No clearance while assets are held; the AD user stays active until the clearance is completed");
    while (await Scalar($"SELECT TOP 1 delivery_id FROM itam_data.delivery WHERE employee_id='{G(12)}' AND received_at_utc IS NULL") is int openDl)
        await store.ReturnAsset(U(1), openDl, new(null, "Working", 1, null), default);
    Check(await Throws<ItamAccessDenied>(() => store.CompleteClearance(U(1), cl, new("HR form"), default)), "Only the System Administrator or the IT Manager completes a clearance");
    await store.CompleteClearance(U(2), cl, new("HR clearance form signed"), default);
    Check(await Scalar($"SELECT disable_status FROM itam_data.clearance WHERE clearance_id={cl}") as string == "Waiting" && await Throws<ItamAccessDenied>(() => store.UserDisabled(U(1), cl, new(null), default)),
        "Completed: one task for the System Administrator to disable the AD user");
    await store.UserDisabled(U(5), cl, new("Disabled and kept"), default);
    var cl2 = NewId(await store.MarkResigned(U(5), new(G(10), null, null), default));
    await store.CancelClearance(U(5), cl2, new("Resignation withdrawn"), default);
    Check(await Scalar($"SELECT disable_status FROM itam_data.clearance WHERE clearance_id={cl}") as string == "Done"
        && await Scalar($"SELECT COUNT(*) FROM itam_data.delivery WHERE employee_id='{G(10)}' AND owner_type=N'Resigned'") is 0, "AD user disabled (kept); a cancelled clearance restores the owner types");

    // AD computer comparison (a test directory; nothing is read from or written to AD).
    var openName = (string)(await Scalar("SELECT TOP 1 computer_name FROM itam_data.delivery WHERE received_at_utc IS NULL AND computer_name IS NOT NULL AND owner_type<>N'Other' ORDER BY delivery_id"))!;
    var waitName = await Scalar("SELECT TOP 1 d.computer_name FROM itam_data.task k JOIN itam_data.delivery d ON d.delivery_id=k.delivery_id WHERE k.kind=N'RemoveFromAd' AND k.done_at_utc IS NULL AND d.computer_name IS NOT NULL") as string;
    var dir = new TestComputers([new(openName.ToLowerInvariant(), "OU=Computers", true, DateTime.UtcNow.AddDays(-1), null, "Win11", true), new("ATWK-EXTRA", "OU=Computers", true, DateTime.UtcNow, null, "Win11", true),
        .. waitName is null ? Array.Empty<ItamAdComputer>() : [new ItamAdComputer(waitName, "OU=Computers", true, DateTime.UtcNow, null, "Win11", true)]]);
    Check(await Throws<ItamAccessDenied>(() => store.RunAdComparison(U(4), dir, default)), "Specialists do not run the comparison");
    await store.RunAdComparison(U(5), dir, default);
    var adLines = JsonSerializer.SerializeToElement(await store.AdComparison(U(5), null, default)).GetProperty("lines").EnumerateArray().ToList();
    string ResultOf(string n) => adLines.First(l => l.GetProperty("computerName").GetString()!.Equals(n, StringComparison.OrdinalIgnoreCase)).GetProperty("result").GetString()!;
    Check(ResultOf(openName) == "Matched" && ResultOf("ATWK-EXTRA") == "NoDelivery" && (waitName is null || ResultOf(waitName) == "RemovalWaiting"),
        "Names compare without case: matched, in AD with no delivery note, removal waiting");
    await store.ReviewAdLine(U(5), new(adLines.First(l => l.GetProperty("result").GetString() == "NoDelivery").GetProperty("id").GetInt32(), "Training computer"), default);
    await store.RunAdComparison(U(5), dir, default);
    Check(JsonSerializer.SerializeToElement(await store.AdComparison(U(1), null, default)).GetProperty("lines").EnumerateArray().Any(l => l.GetProperty("computerName").GetString() == "ATWK-EXTRA" && l.GetProperty("reviewed").GetBoolean()),
        "A reviewed difference stays reviewed in the next run while the computer is unchanged");

    // Licenses: software licenses with seats and holders; the key itself is never in the list.
    await store.Register(U(1), new ItamAssetInput("SOF", 1, null, null, null, null, null, null, null, "New", eDay, null, null, null, null, null, new() { ["Software"] = "Office", ["Seats"] = "3" }, null, "SECRET-KEY-1"), default);
    var lic = JsonSerializer.SerializeToElement(await store.Licenses(U(1), default));
    Check(lic.GetProperty("summary").EnumerateArray().First(x => x.GetProperty("software").GetString() == "Office").GetProperty("seats").GetInt32() == 3 && !lic.ToString().Contains("SECRET-KEY-1") && await Throws<ItamAccessDenied>(() => store.Licenses(U(10), default)),
        "Licenses list: seats and key recorded, never the key itself; IT only");
    Console.WriteLine("PASS end of life");
    // Reports and dashboard: every report opens for the IT Manager; visibility kept; Excel export is a real workbook.
    foreach (var key in new[] { "assets", "by-type", "by-location", "employees", "ageing", "available-computers", "available-computers-list", "unsigned", "leavers", "maintenance", "ink", "pos", "movements", "sales", "disposals", "loss" })
        Check((await store.Report(U(2), key, null, null, null, default)).Columns.Length > 0, $"Report {key} opens");
    var avail = await store.Report(U(2), "available-computers", null, null, null, default);
    Check(Convert.ToInt32(avail.Rows[^1][^1]) == Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM itam_data.asset WHERE type_code=N'PC' AND status=N'InStore'"))
        && (await store.Report(U(2), "available-computers-list", null, null, null, default)).Rows.Count == Convert.ToInt32(avail.Rows[^1][^1])
        && JsonSerializer.SerializeToElement(await store.Dashboard(U(2), default)).GetProperty("computers").GetArrayLength() > 0, "Available computers by computer type: every computer In Store is counted, in the report, the list and the dashboard");
    var westAssets = await store.Report(U(4), "assets", null, null, null, default);
    var regionCol = Array.IndexOf(westAssets.Columns, "Region");
    Check(westAssets.Rows.Count > 0 && westAssets.Rows.All(r => (string?)r[regionCol] == "West") && await Throws<ItamAccessDenied>(() => store.Reports(U(10), default))
        && !(await store.Report(U(6), "assets", null, null, null, default)).Columns.Contains("Unit cost (SAR)") && (await store.Report(U(2), "assets", null, null, null, default)).Columns.Contains("Unit cost (SAR)"),
        "Reports keep the visibility: a specialist sees their regions, costs only for purchase roles, employees have no reports");
    var repByType = await store.Report(U(2), "by-type", null, null, null, default);
    Check((string?)repByType.Rows[^1][0] == "Total" && Convert.ToInt32(repByType.Rows[^1][^1]) == repByType.Rows.Take(repByType.Rows.Count - 1).Sum(r => Convert.ToInt32(r[^1])), "Assets by type: the total row adds up");
    var xl = await store.ReportExcel(U(2), "assets", null, null, null, default);
    using (var zipIn = new System.IO.Compression.ZipArchive(new MemoryStream(xl.Content)))
    {
        var sheetXml = new StreamReader(zipIn.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
        Check(xl.FileName.EndsWith(".xlsx") && zipIn.GetEntry("xl/workbook.xml") is not null && sheetXml.Contains("Asset No.") && sheetXml.Contains("autoFilter"), "Excel export: a workbook with the heading row and filters");
    }
    var dash = JsonSerializer.SerializeToElement(await store.Dashboard(U(2), default));
    Check(dash.GetProperty("tiles").GetProperty("assets").GetInt32() > 0 && await Throws<ItamAccessDenied>(() => store.Dashboard(U(10), default)), "Dashboard for IT, with the asset counts");
    // Alerts: everything waiting for the person, on one page; the menu shows the same total.
    var alMon = NewId(await store.Register(U(1), A("MON", 1, 2, "ALERT-MON-1"), default));
    var alDs = NewId(await store.RequestDisposal(U(1), new((string)(await Scalar($"SELECT asset_no FROM itam_data.asset WHERE asset_id={alMon}"))!, "Old", "Worn"), default));
    bool HasAlert(JsonElement a, string key) => a.GetProperty("groups").EnumerateArray().Any(g => g.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("key").GetString() == key));
    var mgrAlerts = JsonSerializer.SerializeToElement(await store.Alerts(U(2), default));
    Check(HasAlert(mgrAlerts, "disposals") && !HasAlert(JsonSerializer.SerializeToElement(await store.Alerts(U(1), default)), "disposals")
        && JsonSerializer.SerializeToElement(await store.Me(U(2), default)).GetProperty("alerts").GetInt32() == mgrAlerts.GetProperty("total").GetInt32()
        && JsonSerializer.SerializeToElement(await store.Alerts(U(10), default)).GetProperty("total").GetInt32() >= 0, "Alerts: the IT Manager sees the disposal to decide, not the person who asked; the menu shows the same total");
    await store.CancelDisposal(U(1), alDs, new("Alert check"), default);

    // ---- Emails (IT-EM templates, CMS layout): outbox in the action's transaction, once per event; sending after commit ---------------
    var em31 = await Scalar("SELECT TOP 1 to_list+N'|'+subject FROM itam_data.mail_outbox WHERE template=N'IT-EM-31' ORDER BY mail_id") as string;
    var body31 = await Scalar("SELECT TOP 1 body_html FROM itam_data.mail_outbox WHERE template=N'IT-EM-31' ORDER BY mail_id") as string;
    Check(em31 is not null && em31.StartsWith("u12@example.invalid; u3@example.invalid|[IT Assets] Device request approval by the manager - PR-") && body31!.Contains("Dear Colleagues,") && body31.Contains("cid:logo")
        && body31.Contains("IT Assets Management System") && !body31.Contains("<script"), "A device request writes IT-EM-31 to the direct manager and the manager's backup on leave, in the CMS layout");
    // One-click approval from the email: links only read; the decision is a signed-in check of the approver and of the step.
    var qpr = JsonSerializer.SerializeToElement(await store.CreateRequest(U(10), new(null, "New", null, "A docking station"), null, default)).GetProperty("id").GetInt32();
    var qBody = await Scalar($"SELECT body_html FROM itam_data.mail_outbox WHERE template=N'IT-EM-31' AND event_key LIKE N'%|PR-%' ORDER BY mail_id DESC") as string;
    var qInfo = JsonSerializer.SerializeToElement(await store.QuickDecisionInfo(U(1), "request", qpr, "WaitingRequestApproval", default));
    Check(qBody!.Contains($"decide.html?k=request&amp;id={qpr}&amp;s=WaitingRequestApproval&amp;a=approve") && qBody.Contains("#2E7D32") && qInfo.GetProperty("waiting").GetBoolean() && !qInfo.GetProperty("eligible").GetBoolean()
        && await Throws<ItamAccessDenied>(() => store.QuickDecide(U(1), new("request", qpr, "WaitingRequestApproval", true, null), default))
        && await Throws<ArgumentException>(() => store.QuickDecide(U(12), new("request", qpr, "WaitingCostApproval", true, null), default))
        && await Throws<ArgumentException>(() => store.QuickDecide(U(12), new("request", qpr, "WaitingRequestApproval", false, null), default)),
        "Approval emails carry Approve and Reject; only the approver decides, only at the step the email was for, and a rejection needs a reason");
    var qDone = JsonSerializer.SerializeToElement(await store.QuickDecide(U(12), new("request", qpr, "WaitingRequestApproval", true, null), default));
    Check(!qDone.GetProperty("waiting").GetBoolean() && await Scalar($"SELECT status FROM itam_data.request WHERE request_id={qpr}") as string == "InReview"
        && await Throws<ArgumentException>(() => store.QuickDecide(U(12), new("request", qpr, "WaitingRequestApproval", true, null), default)),
        "One click on Approve records the approval; the same email cannot act twice");
    var em01 = (await Scalar("SELECT TOP 1 reply_to+N'|'+body_html FROM itam_data.mail_outbox WHERE template=N'IT-EM-01' ORDER BY mail_id") as string)!;
    Check(em01.StartsWith("u1@example.invalid|") && !em01.Contains("Questions?") && !em01.Contains("please don't reply"),
        "The delivery note email replies to the IT person who issued it, not to the system address");
    foreach (var t in new[] { "IT-EM-18", "IT-EM-26", "IT-EM-27", "IT-EM-19", "IT-EM-20", "IT-EM-32", "IT-EM-09", "IT-EM-10", "IT-EM-41", "IT-EM-42", "IT-EM-44", "IT-EM-11", "IT-EM-64", "IT-EM-01", "IT-EM-02", "IT-EM-13" })
        Check(Convert.ToInt32(await Scalar($"SELECT COUNT(*) FROM itam_data.mail_outbox WHERE template=N'{t}'")) > 0, $"{t} is written by its action");
    Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM (SELECT event_key FROM itam_data.mail_outbox GROUP BY event_key HAVING COUNT(*)>1) x")) == 0, "Each event writes its email once");
    var queuedBefore = Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM itam_data.mail_outbox WHERE status=N'Queued'"));
    var mailer = new TestMailer();
    var sentNow = 0; for (var i = 0; i < 100 && await Scalar("SELECT COUNT(*) FROM itam_data.mail_outbox WHERE status=N'Queued'") is int n && n > 0; i++) sentNow += await store.SendQueuedMail(mailer, "it-test@example.invalid", [1, 2, 3], default);
    Check(sentNow == queuedBefore && mailer.Sent.All(m => m.To.SequenceEqual(new[] { "it-test@example.invalid" }) && m.Cc.Count == 0 && m.Html.Contains("TEST: this email was for") && m.Images.Count == 1)
        && Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM itam_data.mail_outbox WHERE status=N'Sent'")) == sentNow, "Sent after commit through the mail service; the test mailbox receives every email with the original recipients shown");
    var leaveFor = await store.MarkResigned(U(5), new(G(10), null, null), default);
    var failId = (long)(await Scalar("SELECT MAX(mail_id) FROM itam_data.mail_outbox WHERE status=N'Queued'"))!;
    for (var i = 0; i < 5; i++) await store.SendQueuedMail(new FailingMailer(), null, [], default);
    Check(await Scalar($"SELECT status+N'|'+CAST(attempts AS nvarchar(5)) FROM itam_data.mail_outbox WHERE mail_id={failId}") as string == "Failed|5"
        && Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM itam_data.mail_outbox WHERE template=N'IT-EM-56' AND event_key LIKE N'%Failed%'")) == 0, "A mail service failure is retried 5 times, then Failed on the outbox page, never emailed");
    await store.RetryMail(U(2), failId, default);
    Check(await Scalar($"SELECT status FROM itam_data.mail_outbox WHERE mail_id={failId}") as string == "Queued" && await Throws<ItamAccessDenied>(() => store.MailOutbox(U(1), null, default)), "The IT Manager sends a failed email again; only the IT Manager sees the outbox");
    await store.CancelClearance(U(5), JsonSerializer.SerializeToElement(leaveFor).GetProperty("id").GetInt32(), new("Check"), default);
    await Exec($"UPDATE itam_access.employee SET email=NULL WHERE employee_id='{G(3)}'");
    var noMailMon = JsonSerializer.SerializeToElement(await store.Register(U(1), A("MON", 1, 2, "MAIL-MON-1"), default)).GetProperty("id").GetInt32();
    await store.CreateMaintenanceOrder(U(2), new((string)(await Scalar($"SELECT asset_no FROM itam_data.asset WHERE asset_id={noMailMon}"))!, 1, "Q-MAIL", 10m, "Check", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5))), scan, default);
    Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM itam_data.mail_outbox WHERE template=N'IT-EM-09' AND subject LIKE N'%Q-MAIL%'")) == 0
        && await Scalar("SELECT TOP 1 to_list FROM itam_data.mail_outbox WHERE template=N'IT-EM-09' ORDER BY mail_id DESC") is string to09 && !to09.Contains("u2@") && !to09.Contains("u3@"),
        "An approval email is not sent to the person who acted; a person without an email address is left out");
    var sunday = DateTime.UtcNow.Date; while (sunday.DayOfWeek != DayOfWeek.Sunday) sunday = sunday.AddDays(1);
    var weekly = await store.RunMailJobs(sunday.AddHours(6), default);
    Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM itam_data.mail_outbox WHERE template=N'IT-EM-12'")) > 0 && await Scalar("SELECT TOP 1 reply_to FROM itam_data.mail_outbox WHERE template=N'IT-EM-12'") is null
        && (await Scalar("SELECT TOP 1 body_html FROM itam_data.mail_outbox WHERE template=N'IT-EM-12'") as string)!.Contains("please don't reply") && await store.RunMailJobs(sunday.AddHours(7), default) == 0,
        "Weekly summary to the IT Manager (IT-EM-12) and the reminders are written once per period");
    Console.WriteLine("PASS emails");

    // ---- Production host pieces: Windows sign-in, AD mapping, SMTP email, install check -------------------------------------------
    await using (var c = new SqlConnection(cs)) Check(await ItamSchemaInstaller.Pending(c) == 0, "No database script is pending after the install");
    async Task<ItamUser?> SignIn(string name) { var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Principal.GenericIdentity(name, "Negotiate")) }; return await new ItamWindowsSession(store).Current(ctx, default); }
    Check(ItamWindowsSession.Account(@"ALTAWKILAT\U10") == "u10" && ItamWindowsSession.Account("u10@altawkilat.com") == "u10" && (await SignIn(@"ALTAWKILAT\u10"))?.Id == G(10)
        && await SignIn(@"ALTAWKILAT\u11") is null && await SignIn(@"ALTAWKILAT\nobody") is null && await new ItamWindowsSession(store).Current(new Microsoft.AspNetCore.Http.DefaultHttpContext(), default) is null,
        "Windows sign-in: the enabled employee with the same account; a disabled or unknown account is not let in");
    // The sign-in page end to end (loopback web server, test password check: "right-pass").
    {
        var web = WebApplication.CreateSlimBuilder();
        web.WebHost.UseUrls("http://127.0.0.1:0");
        web.Logging.ClearProviders();
        web.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest; });
        web.Services.AddScoped(_ => new ItamStore(Runtime, new FakeSecrets()) { Pdf = pdf });
        web.Services.AddScoped<IItamSession, ItamWindowsSession>();
        web.Services.AddSingleton<IItamPasswordCheck, TestPasswords>();
        web.Services.AddItamSignIn(httpsOnly: false);
        await using var site = web.Build();
        site.UseAuthentication(); site.MapItamSignIn(); site.MapItam();
        await site.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(site.Urls.First()) };
        var token = JsonDocument.Parse(await http.GetStringAsync("/api/signin")).RootElement.GetProperty("csrf").GetString();
        async Task<(int Status, string Body)> Post(string path, object? body)
        {
            using var m = new HttpRequestMessage(HttpMethod.Post, path) { Content = System.Net.Http.Json.JsonContent.Create(body) };
            m.Headers.Add("X-CSRF-TOKEN", token);
            using var r = await http.SendAsync(m); return ((int)r.StatusCode, await r.Content.ReadAsStringAsync());
        }
        var noCsrf = (int)(await http.PostAsync("/api/signin", System.Net.Http.Json.JsonContent.Create(new { account = "u10", password = "right-pass" }))).StatusCode;
        var signedOut = (int)(await http.GetAsync("/api/me")).StatusCode;
        var empty = await Post("/api/signin", new { account = "u10", password = "" });
        var wrong = new List<int>(); for (var i = 0; i < 5; i++) wrong.Add((await Post("/api/signin", new { account = "u12", password = "wrong" })).Status);
        var locked = await Post("/api/signin", new { account = "u12", password = "right-pass" });
        var disabled = await Post("/api/signin", new { account = "u11", password = "right-pass" });
        var good = await Post("/api/signin", new { account = @"ALTAWKILAT\U10", password = "right-pass", @return = "/request.html?no=PR-1" });
        var meAfter = await http.GetStringAsync("/api/me");
        token = JsonDocument.Parse(meAfter).RootElement.GetProperty("csrf").GetString();
        var outStatus = (await Post("/api/signout", null)).Status;
        var after = (int)(await http.GetAsync("/api/me")).StatusCode;
        await site.StopAsync();
        Check(noCsrf == 400 && signedOut == 401 && empty.Status == 400 && wrong.All(s => s == 401) && locked.Status == 429 && disabled.Status == 403
            && good.Status == 200 && good.Body.Contains("/request.html?no=PR-1") && meAfter.Contains(G(10).ToString()) && outStatus == 200 && after == 401,
            $"Sign-in page: needs the page token; wrong passwords refused, then the account waits (AD is not asked); a disabled employee is not let in; the right password signs in, returns to the page and Sign out ends it ({noCsrf} {signedOut} {empty.Status} {string.Join(',', wrong)} {locked.Status} {disabled.Status} {good.Status} {outStatus} {after})");
        Check(ItamSignIn.ReturnTo("//evil.example/x") == "/" && ItamSignIn.ReturnTo(@"/\evil") == "/" && ItamSignIn.ReturnTo("https://evil.example") == "/" && ItamSignIn.ReturnTo("/signin.html") == "/"
            && ItamSignIn.ReturnTo("/movement.html?no=GP-1") == "/movement.html?no=GP-1" && ItamSignIn.Account(" u10@altawkilat.com ") == "u10" && ItamSignIn.Account("  ") is null,
            "Sign-in page: returns only to a page of this site; the username may be typed as user, DOMAIN\\user or user@domain");
    }
    var adUser = new Dictionary<string, string?> { ["sAMAccountName"] = "Ahmed.Ali", ["displayName"] = "Ahmed Ali", ["mail"] = "ahmed.ali@example.invalid", ["description"] = "Accountant", ["department"] = "Finance",
        ["company"] = "Demo Co", ["physicalDeliveryOfficeName"] = "Head Office", ["postalCode"] = "Central", ["postOfficeBox"] = "HR-1", ["streetAddress"] = "SAP-9", ["mobile"] = "0500000000", ["ipPhone"] = "4100",
        ["manager"] = "CN=Boss,OU=Users,DC=x", ["userAccountControl"] = "514", ["extensionAttribute6"] = "1234567890" };
    var adBoss = Guid.NewGuid(); var adEmp = ItamDirectoryMapping.Employee(Guid.NewGuid(), k => adUser.GetValueOrDefault(k), dn => dn == "CN=Boss,OU=Users,DC=x" ? adBoss : null)!;
    Check(adEmp.Account == "ahmed.ali" && adEmp.ManagerId == adBoss && !adEmp.Enabled && adEmp.Region == "Central" && adEmp.SapId == "SAP-9" && adEmp.IpPhone == "4100" && adEmp.HrCode == "HR-1"
        && !ItamDirectoryMapping.UserAttributes.Contains("extensionAttribute6") && ItamDirectoryMapping.Employee(Guid.NewGuid(), _ => null, _ => null) is null,
        "AD user to employee: the VTMS attributes, the manager by DN, disabled accounts; the national ID is never read");
    List<(string, bool)> ous = [("OU=Computers,OU=ATWK,DC=x", true), ("CN=Computers,DC=x", false)];
    ItamAdComputer? Pc(string dn, string uac = "4096") => ItamDirectoryMapping.Computer(k => k switch { "name" => dn[3..dn.IndexOf(',')], "distinguishedName" => dn, "userAccountControl" => uac, "lastLogonTimestamp" => "133000000000000000", "whenCreated" => "20240115083000.0Z", _ => null }, ous, [new System.Text.RegularExpressions.Regex("^SRV")]);
    Check(Pc("CN=PC1,OU=Sales,OU=Computers,OU=ATWK,DC=x")!.InComparedOus && Pc("CN=PC2,CN=Computers,DC=x")!.InComparedOus && !Pc("CN=PC3,OU=Old,DC=x")!.InComparedOus && Pc("CN=SRV1,CN=Computers,DC=x") is null
        && !Pc("CN=PC4,CN=Computers,DC=x", "4098")!.Enabled && Pc("CN=PC1,OU=Sales,OU=Computers,OU=ATWK,DC=x")!.LastLogonUtc?.Year == 2022 && Pc("CN=PC1,OU=Sales,OU=Computers,OU=ATWK,DC=x")!.CreatedUtc?.Year == 2024,
        "AD computer: compared OUs with or without sub-OUs, left-out names, disabled, last sign-in and creation dates");
    var pickup = Path.Combine(Path.GetTempPath(), "itam-mail-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(pickup);
    try
    {
        await new ItamSmtpMailer(new ItamSmtpSettings("relay.invalid", 25, false, "itassets@example.invalid", "IT Assets", PickupDirectory: pickup))
            .Send(new ItamMailMessage(["a@example.invalid"], ["b@example.invalid"], "[IT Assets] Test", "<p>Dear <b>Ahmed</b></p><img src=\"cid:logo\">", [new ItamMailImage("logo", "logo.png", "image/png", [137, 80, 78, 71])], "it.person@example.invalid"), default);
        var eml = await File.ReadAllTextAsync(Directory.GetFiles(pickup).Single());
        Check(eml.Contains("Reply-To: it.person@example.invalid") && eml.Contains("Content-ID: <logo>") && eml.Contains("To: a@example.invalid") && eml.Contains("Cc: b@example.invalid") && eml.Contains("text/plain"),
            "SMTP email: To, CC, Reply-To (the person who acted), the logo embedded and a plain-text copy");
    }
    finally { Directory.Delete(pickup, true); }
    Console.WriteLine("PASS production host");

    // ---- My Backup ----------------------------------------------------------------------------------------------------------------
    var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
    await store.SetBackup(U(1), new(null, G(3), today.AddDays(1), today.AddDays(5), "Annual leave"), default);
    Check(await Throws<ArgumentException>(() => store.SetBackup(U(1), new(null, G(4), today.AddDays(3), today.AddDays(8), null), default)), "Overlapping backups are refused");
    Check(await Throws<ArgumentException>(() => store.SetBackup(U(3), new(null, G(1), today.AddDays(2), today.AddDays(3), null), default)), "A backup on leave the same dates cannot be chosen");
    Check(await Throws<ArgumentException>(() => store.SetBackup(U(1), new(null, G(1), today.AddDays(10), today.AddDays(11), null), default)), "You cannot be your own backup");
    Check(await Throws<ItamAccessDenied>(() => store.SetBackup(U(1), new(G(3), G(4), today.AddDays(10), today.AddDays(11), null), default)), "The IT Assets Officer cannot set backups for others (Team Leader, System Administrator and IT Manager do)");
    var mine = JsonSerializer.SerializeToElement(await store.Backups(U(1), default)).GetProperty("items")[0];
    Check(mine.GetProperty("status").GetString() == "Upcoming", "Backup shown as upcoming");
    await store.CancelBackup(U(1), mine.GetProperty("id").GetInt32(), default);
    Check(J(await store.Backups(U(2), default)).Contains("Cancelled"), "Cancelled backup stays in the IT Manager's list");
    Check(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM itam_access.audit")) >= 8, "Changes are in the audit log");
    Console.WriteLine("PASS backups and audit");

    // ---- CSV import and export (Administration Center) --------------------------------------------------------------------
    ItamUpload CsvFile(string text, string name = "lists.csv") => new(name, "text/csv", System.Text.Encoding.UTF8.GetBytes(text));
    string CsvText(ItamUpload f) => System.Text.Encoding.UTF8.GetString(f.Content);
    var supCsv = await store.ExportCsv(U(2), "suppliers", default);
    Check(supCsv.Content[0] == 0xEF && CsvText(supCsv).Contains("Name,Arabic name,VAT number,Active\r\n") && CsvText(supCsv).Contains("Supplier Two,,300000000000003,Yes") && supCsv.FileName.EndsWith(".csv"),
        "CSV export: UTF-8 for Excel, the column names, one row per supplier");
    // Supplier Arabic name (owner, 22 October 2026): Administration Center form and CSV.
    await store.Change(U(2), new("supplier", "save", E(new { name = "Arabic Supplier", nameAr = "شركة المورد التجريبية", vat = "300000000000099", active = true }), null), default);
    var arCsv = CsvText(await store.ExportCsv(U(2), "suppliers", default));
    var arKeep = await store.ImportCsv(U(2), "suppliers", new(false, null), CsvFile("Name,Active\r\nArabic Supplier,Yes\r\n"), default);
    var keptAr = await Scalar("SELECT name_ar FROM itam_ref.supplier WHERE name_en=N'Arabic Supplier'") as string;
    var arSet = await store.ImportCsv(U(2), "suppliers", new(false, null), CsvFile("Name,Arabic name\r\nArabic Supplier,مورد معدل\r\n"), default);
    Check(arCsv.Contains("Arabic Supplier,شركة المورد التجريبية,300000000000099,Yes") && J(await store.Admin(U(2), default)).Contains("nameAr")
        && arKeep.Unchanged == 1 && keptAr == "شركة المورد التجريبية" && arSet.Changed == 1
        && await Scalar("SELECT name_ar FROM itam_ref.supplier WHERE name_en=N'Arabic Supplier'") as string == "مورد معدل"
        && await Scalar("SELECT vat_no FROM itam_ref.supplier WHERE name_en=N'Arabic Supplier'") as string == "300000000000099",
        "Supplier Arabic name: saved in the Administration Center, exported, and changed by CSV; a column left out of the file keeps its value");
    var badLoc = CsvFile("Name,Kind,Region,City,Active\r\nCSV Store A,IT warehouse,Central,Riyadh,Yes\r\nCSV Store B,Shop,Central,,Yes\r\n");
    var c1 = await store.ImportCsv(U(2), "locations", new(true, null), badLoc, default);
    var c2 = await store.ImportCsv(U(2), "locations", new(false, null), badLoc, default);
    Check(c1.Errors.Count == 1 && c1.Errors[0].Row == 3 && c1.Errors[0].Error.Contains("Kind") && !c1.Saved && !c2.Saved
        && await Scalar("SELECT 1 FROM itam_ref.location WHERE name_en=N'CSV Store A'") is null,
        "CSV import: a wrong row is listed with its Excel row number, and nothing is saved");
    var goodLoc = CsvFile("Region;Name;Kind;City\r\nCentral;CSV Store A;IT warehouse;Riyadh\r\nCentral;\"CSV Store, B\";Branch;\r\n");
    var c3 = await store.ImportCsv(U(2), "locations", new(true, null), goodLoc, default);
    var noneYet = await Scalar("SELECT COUNT(*) FROM itam_ref.location WHERE name_en LIKE N'CSV Store%'");
    var c4 = await store.ImportCsv(U(2), "locations", new(false, "first load"), goodLoc, default);
    var c5 = await store.ImportCsv(U(2), "locations", new(false, null), goodLoc, default);
    var c6 = await store.ImportCsv(U(2), "locations", new(false, null), CsvFile("Name,Kind,Region,Active\r\nCSV Store A,IT warehouse,Central,No\r\n"), default);
    Check(c3.Errors.Count == 0 && !c3.Saved && c3.Added == 2 && Convert.ToInt32(noneYet) == 0 && c4.Saved && c4.Added == 2 && c5.Unchanged == 2 && c5.Added + c5.Changed == 0
        && c6.Changed == 1 && await Scalar("SELECT active FROM itam_ref.location WHERE name_en=N'CSV Store A'") is false
        && Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM itam_access.audit WHERE entity=N'location' AND reason=N'CSV import (lists.csv): first load'")) == 2,
        "CSV import: check only saves nothing; columns in any order, semicolons and quotes; rows added, then already the same, then changed; each in the audit log");
    await store.Change(U(2), new("supplier", "save", E(new { name = "=1+2", active = true }), null), default);
    var guarded = CsvText(await store.ExportCsv(U(2), "suppliers", default));
    var reimport = await store.ImportCsv(U(2), "suppliers", new(false, null), new("s.csv", "text/csv", System.Text.Encoding.UTF8.GetBytes(guarded)), default);
    Check(guarded.Contains("'=1+2,,,Yes") && reimport.Errors.Count == 0 && reimport.Unchanged == reimport.Rows,
        "CSV export never lets Excel run a cell as a formula; the exported file imports back unchanged");
    var models = await store.ImportCsv(U(2), "models", new(true, null), CsvFile("Manufacturer,Type,Model\r\nNo Such Maker,PC,X1\r\n"), default);
    Check(models.Errors.Count == 1 && models.Errors[0].Error.Contains("Manufacturer"), "CSV import of models: the manufacturer must be in the list first");
    Check(await Throws<ItamAccessDenied>(() => store.ImportCsv(U(3), "locations", new(true, null), goodLoc, default))
        && await Throws<ItamAccessDenied>(() => store.ImportCsv(U(5), "roles", new(true, null), CsvFile("Username,Role\r\nu10,Team Leader\r\n"), default))
        && await Throws<ArgumentException>(() => store.ImportCsv(U(2), "employees", new(true, null), CsvFile("Username\r\nu10\r\n"), default))
        && await Throws<ArgumentException>(() => store.ImportCsv(U(2), "locations", new(true, null), CsvFile("Place,Kind\r\nX,Branch\r\n"), default))
        && await Throws<ArgumentException>(() => store.ImportCsv(U(2), "locations", new(true, null), CsvFile("Name,Kind,Region\r\n", "lists.xlsx"), default)),
        "CSV import: only the IT Manager imports; employees come from AD only; the column names and a .csv file are required");
    var roles = await store.ImportCsv(U(2), "roles", new(false, null), CsvFile("Username,Name,Role,Region\r\nU10,Anyone,Ink Officer,Central\r\nu11,,Team Leader,\r\n"), default);
    var roles2 = await store.ImportCsv(U(2), "roles", new(false, null), CsvFile("Username,Name,Role,Region\r\nALTAWKILAT\\u10,,InkOfficer,Central\r\n"), default);
    Check(roles.Errors.Count == 1 && roles.Errors[0].Row == 3 && !roles.Saved && roles2.Saved && roles2.Added == 1
        && await Scalar($"SELECT 1 FROM itam_access.role_assignment WHERE employee_id='{G(10)}' AND role_code=N'InkOfficer' AND region_code=N'Central'") is not null
        && CsvText(await store.ExportCsv(U(2), "roles", default)).Contains("u10,") && CsvText(await store.ExportCsv(U(2), "audit", default)).Contains("CSV import"),
        "CSV import of roles: by username and role name; a disabled employee is refused; roles and the audit log export");
    Console.WriteLine("PASS CSV import and export");
    // Review copies of the emails (only when ITAM_MAIL_SAMPLES names a folder): the latest email of each template, as sent,
    // with the logo embedded so each file opens on its own. Invented check data only.
    if (Environment.GetEnvironmentVariable("ITAM_MAIL_SAMPLES") is { Length: > 0 } sampleDir)
    {
        Directory.CreateDirectory(sampleDir);
        // Extra examples for the review only: overdue reminders (dates moved back) and a transfer received in part.
        await Exec("UPDATE TOP(1) itam_data.movement SET status=N'Out',pass_type=N'Returnable',expected_date=DATEADD(day,-2,CAST(SYSUTCDATETIME() AS date)) WHERE kind=N'Exit'");
        await Exec("UPDATE TOP(1) itam_data.disposal SET status=N'Approved',decided_at_utc=DATEADD(day,-20,SYSUTCDATETIME()) WHERE status IN(N'Rejected',N'Cancelled',N'Completed')");
        await Exec("UPDATE TOP(1) itam_data.maintenance_order SET status=N'WithSupplier',expected_return=DATEADD(day,-3,CAST(SYSUTCDATETIME() AS date)) WHERE status IN(N'Closed',N'Rejected',N'Cancelled')");
        await Exec("UPDATE TOP(1) itam_data.loss_case SET status=N'AwaitingDecision',findings_at_utc=DATEADD(day,-10,SYSUTCDATETIME())");
        var sp1 = JsonSerializer.SerializeToElement(await store.Register(U(1), A("MON", 1, 2, "SAMPLE-P1"), default)).GetProperty("id").GetInt32();
        var sp2 = JsonSerializer.SerializeToElement(await store.Register(U(1), A("MON", 1, 2, "SAMPLE-P2"), default)).GetProperty("id").GetInt32();
        var spw = JsonSerializer.SerializeToElement(await store.CreateMovement(U(9), Wt([sp1, sp2], G(4)), default)).GetProperty("id").GetInt32();
        await store.DispatchMovement(U(9), spw, new(null), default);
        var spl = (int)(await Scalar($"SELECT MIN(line_id) FROM itam_data.movement_line WHERE movement_id={spw}"))!;
        await store.ReceiveMovement(U(4), spw, new([new(spl, "Received", null)]), default);
        await Exec($"UPDATE itam_data.movement SET expected_date=DATEADD(day,-2,CAST(SYSUTCDATETIME() AS date)) WHERE movement_id={spw}");
        await store.RunMailJobs(DateTime.UtcNow.Date.AddHours(6), default);
        await using var lr = typeof(ItamStore).Assembly.GetManifestResourceStream("Itam.App.Assets.company-logo.png")!; using var lm = new MemoryStream(); await lr.CopyToAsync(lm);
        var logo64 = "data:image/png;base64," + Convert.ToBase64String(lm.ToArray());
        await using var sc = new SqlConnection(cs); await sc.OpenAsync();
        using var sq = new SqlCommand("SELECT o.template,o.subject,o.body_html FROM itam_data.mail_outbox o WHERE o.mail_id=(SELECT MAX(mail_id) FROM itam_data.mail_outbox x WHERE x.template=o.template) ORDER BY o.template", sc);
        await using var sr = await sq.ExecuteReaderAsync();
        while (await sr.ReadAsync()) await File.WriteAllTextAsync(Path.Combine(sampleDir, sr.GetString(0) + ".html"), sr.GetString(2).Replace("cid:logo", logo64));
    }
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

sealed class TestComputers(ItamAdComputer[] list) : IItamComputerDirectory
{
    public string ComparedOus => "OU=Computers (test)";
    public Task<IReadOnlyList<ItamAdComputer>> Computers(CancellationToken token) => Task.FromResult<IReadOnlyList<ItamAdComputer>>(list);
}

sealed class TestMailer : IItamMailer
{
    public List<ItamMailMessage> Sent { get; } = [];
    public Task Send(ItamMailMessage message, CancellationToken token) { Sent.Add(message); return Task.CompletedTask; }
}
sealed class FailingMailer : IItamMailer
{
    public Task Send(ItamMailMessage message, CancellationToken token) => throw new InvalidOperationException("SMTP not reachable (test)");
}

sealed class TestPasswords : IItamPasswordCheck
{
    public Task<bool> Check(string account, string password, CancellationToken token) => Task.FromResult(password == "right-pass");
}
