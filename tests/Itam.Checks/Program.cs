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
    await using (var c = new SqlConnection(cs)) Check(await ItamSchemaInstaller.Apply(c, name) == 1, "Migration applied");
    await using (var c = new SqlConnection(cs)) Check(await ItamSchemaInstaller.Apply(c, name) == 0, "Second run applies nothing");
    var font = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"));
    var pdf = new ItamPdf(font);
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
        INSERT itam_ref.company(company_code,name_en) VALUES(N'CO',N'Company');
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
    Check(await Throws<ItamAccessDenied>(() => store.Change(U(1), new("role", "save", E(new { employee = G(10), role = "Auditor" }), null), default)), "Only the IT Manager and IT Administrator give roles");
    Console.WriteLine("PASS roles and master data");

    // ---- Register ----------------------------------------------------------------------------------------------------------------
    ItamAssetInput A(string type, int loc, int model, string? serial, string? tag = null, string? mac = null, Dictionary<string, string?>? at = null, string? osKey = null, int mf = 1)
        => new(type, "CO", loc, mf, model, null, serial, tag, mac, null, "New", new DateOnly(2026, 1, 10), null, null, null, null, null, null, at, osKey, null);
    var pcAttrs = new Dictionary<string, string?> { ["ComputerType"] = "Notebook", ["OperatingSystem"] = "Win11", ["MemoryGb"] = "16" };
    var pc = JsonSerializer.SerializeToElement(await store.Register(U(1), A("PC", 1, 1, "SN-0001", "TAG1", null, pcAttrs, "AAAAA-BBBBB"), default));
    var mon = JsonSerializer.SerializeToElement(await store.Register(U(1), A("MON", 2, 2, "MN-0001"), default));
    Check(pc.GetProperty("assetNo").GetString() == "PC-4809" && mon.GetProperty("assetNo").GetString() == "MON-4810", "Asset No.: product code and one sequence for all types, after 4808");
    Check((await Error(() => store.Register(U(1), A("PC", 1, 1, "sn 0001", "TAG9", null, pcAttrs), default))).Contains("already on PC-4809"), "Duplicate serial number refused (spaces, dashes and case ignored)");
    Check((await Error(() => store.Register(U(1), A("PC", 1, 1, "SN-0002", null, null, pcAttrs), default))).Contains("Service tag"), "Service tag required for computers");
    Check((await Error(() => store.Register(U(1), A("IP", 1, 3, "IP-0001", mf: 2), default))).Contains("MAC"), "MAC address required for IP phones");
    Check((await Error(() => store.Register(U(1), A("MON", 1, 2, null), default))).Contains("Serial"), "Serial number required (except software)");
    Check(await Throws<ArgumentException>(() => store.Register(U(1), A("PC", 1, 1, "SN-0003", "TAG3", null, new() { ["ComputerType"] = "Spaceship" }), default)), "List fields accept list values only");
    Check(await Throws<ArgumentException>(() => store.Register(U(1), A("MON", 1, 2, "MN-0009") with { PurchaseDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)) }, default)), "Purchase date cannot be in the future");
    var phone = JsonSerializer.SerializeToElement(await store.Register(U(6), A("IP", 1, 3, "IP-0002", mac: "aa-bb-cc-dd-ee-ff", mf: 2), default));
    Check(await Scalar($"SELECT mac_address FROM itam_data.asset WHERE asset_id={phone.GetProperty("id").GetInt32()}") as string == "AA:BB:CC:DD:EE:FF", "MAC address stored as AA:BB:CC:DD:EE:FF");
    Check(await Throws<ItamAccessDenied>(() => store.Register(U(4), A("MON", 1, 2, "MN-0100"), default)) && (await store.Register(U(4), A("MON", 2, 2, "MN-0101"), default)) is not null,
        "IT Support Specialist registers in own region only");
    Check(await Throws<ItamAccessDenied>(() => store.Register(U(8), A("MON", 2, 2, "MN-0102"), default)), "The Auditor cannot register");
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
    await store.Update(U(1), pcId, A("PC", 1, 1, "SN-0001", "TAG1", null, new() { ["ComputerType"] = "Notebook", ["OperatingSystem"] = "Win11", ["MemoryGb"] = "32" }) with { Version = version }, default);
    Check(await Scalar($"SELECT TOP 1 detail FROM itam_data.asset_event WHERE asset_id={pcId} AND kind='Edited'") as string is { } h && h.Contains("Memory (GB): 16 → 32"), "History records what changed");
    Check(await Throws<ItamConflict>(() => store.Update(U(1), pcId, A("PC", 1, 1, "SN-0001", "TAG1", null, pcAttrs) with { Version = version }, default)), "A stale edit is refused");
    var phoneId = phone.GetProperty("id").GetInt32();
    var pv = JsonSerializer.SerializeToElement(await store.Asset(U(6), phoneId, default)).GetProperty("asset").GetProperty("version").GetString();
    await store.Update(U(6), phoneId, A("IP", 1, 3, "CHANGED", mac: "11:22:33:44:55:66", mf: 2, at: new() { ["Extension"] = "5000" }) with { Version = pv }, default);
    Check(await Scalar($"SELECT serial_no+'|'+mac_address FROM itam_data.asset WHERE asset_id={phoneId}") as string == "IP-0002|11:22:33:44:55:66"
        && await Scalar($"SELECT value FROM itam_data.asset_attribute WHERE asset_id={phoneId} AND attr_code='Extension'") as string == "5000", "Voice Administrator changes MAC and extension only");
    Console.WriteLine("PASS who sees what, keys, edit, history");

    // ---- Purchase orders ---------------------------------------------------------------------------------------------------------
    var doc = pdf.Start(); doc.Text("Purchase order PO-77 (check document)", 40, doc.Y, 12); var poPdf = doc.Build();
    var poInput = new ItamPoInput("PO-77", new DateOnly(2026, 10, 1), 1, "CO", "Procurement", "pr-3", null, null,
        [new(1, "PC", 1, 1, null, "i7", 3, 1000m), new(2, "MON", 1, 2, null, null, 2, 500m)]);
    Check(await Throws<ItamAccessDenied>(() => store.CreatePo(U(4), poInput, new("po.pdf", "application/pdf", poPdf), default)), "Specialists cannot record POs");
    Check(await Throws<ArgumentException>(() => store.CreatePo(U(1), poInput, null, default)) && await Throws<ArgumentException>(() => store.CreatePo(U(1), poInput, new("po.png", "image/png", [0x89, 0x50, 0x4E, 0x47, 1, 2]), default)),
        "The PO document (PDF) is required");
    var po = JsonSerializer.SerializeToElement(await store.CreatePo(U(1), poInput, new("po.pdf", "application/pdf", poPdf), default)).GetProperty("id").GetInt32();
    Check(await Throws<ArgumentException>(() => store.CreatePo(U(3), poInput, new("po.pdf", "application/pdf", poPdf), default)), "The same PO number twice is refused");
    Check(await Scalar($"SELECT request_ref FROM itam_data.po WHERE po_id={po}") as string == "PR-3", "Request number stored as PR-n");
    int Line(int no) => (int)Scalar($"SELECT po_line_id FROM itam_data.po_line WHERE po_id={po} AND line_no={no}").Result!;
    ItamDeviceInput Dev(string serial, string? tag = null) => new(serial, tag, null, null, "New", new() { ["ComputerType"] = "Notebook" }, null, null, null);
    var r1 = JsonSerializer.SerializeToElement(await store.Receive(U(1), po, new(Line(1), 1, null, "INV-1", null, [Dev("POSN1", "POTAG1"), Dev("POSN2", "POTAG2")]), null, default));
    Check(r1.GetProperty("assets").GetArrayLength() == 2, "Partial receipt: 2 of 3");
    var before = await Scalar("SELECT COUNT(*) FROM itam_data.asset");
    Check((await Error(() => store.Receive(U(1), po, new(Line(1), 1, null, null, null, [Dev("POSN3", "POTAG3"), Dev("POSN4", "POTAG4")]), null, default))).Contains("still to come")
        && Equals(await Scalar("SELECT COUNT(*) FROM itam_data.asset"), before), "Receiving more than the remaining quantity is refused; nothing saved");
    Check((await Error(() => store.Receive(U(1), po, new(Line(2), 1, null, null, null, [new("SAME1", null, null, null, "New", null, null, null, null), new("same-1", null, null, null, "New", null, null, null, null)]), null, default))).Contains("twice")
        && Equals(await Scalar("SELECT COUNT(*) FROM itam_data.asset"), before), "The same serial twice in one receipt is refused; nothing saved");
    Check((await Error(() => store.Receive(U(1), po, new(Line(1), 1, null, null, null, [Dev("SN-0001", "NEWTAG")]), null, default))).Contains("Device 1"), "A serial already in IT Assets is refused, naming the row");
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
