using Itam.App;
using Microsoft.Data.SqlClient;

// Invented demo data, entered as an administrator would, then assets and a PO through the real IT Assets code.
static class DemoSeed
{
    static Guid G(int n) => new($"00000000-0000-0000-0000-{n:000000000000}");

    public static async Task Run(string cs, ItamStore store, ItamPdf pdf)
    {
        await store.SyncEmployees([.. DemoPeople.All.Select(p => new ItamEmployee(p.Id, p.Account, p.Name, p.Account + "@example.invalid", p.Role, "IT (demo)", p.Id == G(2) ? null : G(2), true))], default);
        await using (var c = new SqlConnection(cs))
        {
            await c.OpenAsync();
            using var q = new SqlCommand("""
                INSERT itam_access.role_assignment(employee_id,role_code,region_code,created_by) VALUES
                 ('00000000-0000-0000-0000-000000000001','AssetsOfficer',NULL,'00000000-0000-0000-0000-000000000002'),
                 ('00000000-0000-0000-0000-000000000002','ItManager',NULL,'00000000-0000-0000-0000-000000000002'),
                 ('00000000-0000-0000-0000-000000000003','TeamLeader',NULL,'00000000-0000-0000-0000-000000000002'),
                 ('00000000-0000-0000-0000-000000000004','SupportSpecialist','West','00000000-0000-0000-0000-000000000002'),
                 ('00000000-0000-0000-0000-000000000005','SupportSpecialist','Central','00000000-0000-0000-0000-000000000002'),
                 ('00000000-0000-0000-0000-000000000005','SupportSpecialist','East','00000000-0000-0000-0000-000000000002'),
                 ('00000000-0000-0000-0000-000000000006','SystemAdministrator',NULL,'00000000-0000-0000-0000-000000000002'),
                 ('00000000-0000-0000-0000-000000000007','VoiceAdministrator',NULL,'00000000-0000-0000-0000-000000000002'),
                 ('00000000-0000-0000-0000-000000000008','ItAdministrator',NULL,'00000000-0000-0000-0000-000000000002'),
                 ('00000000-0000-0000-0000-000000000009','Auditor',NULL,'00000000-0000-0000-0000-000000000002');
                INSERT itam_ref.location(name_en,kind,region_code,city) VALUES
                 (N'Riyadh IT Warehouse (demo)','ITWarehouse','Central',N'Riyadh'),(N'Jeddah IT Warehouse (demo)','ITWarehouse','West',N'Jeddah'),
                 (N'Dammam IT Warehouse (demo)','ITWarehouse','East',N'Dammam'),(N'Abha Branch (demo)','Branch','South',N'Abha'),
                 (N'Al Kharj Branch (demo)','Branch','Central',N'Al Kharj'),(N'Dubai Office (demo)','Branch','UAE',N'Dubai');
                INSERT itam_ref.company(company_code,name_en) VALUES(N'DEMO',N'ALTAWKILAT (demo company)');
                INSERT itam_ref.supplier(name_en,vat_no) VALUES(N'Demo Computers Co.','300000000000003'),(N'Demo Network Supplies',NULL),(N'Demo Office Equipment',NULL);
                INSERT itam_ref.manufacturer(name_en) VALUES(N'Dell'),(N'HP'),(N'Lenovo'),(N'Cisco'),(N'Canon'),(N'Epson'),(N'Samsung'),(N'Microsoft');
                INSERT itam_ref.model(manufacturer_id,type_code,name_en) SELECT m.manufacturer_id,v.t,v.n FROM (VALUES
                 (N'Dell','PC',N'Latitude 5440'),(N'Dell','PC',N'OptiPlex 7010'),(N'HP','PC',N'EliteBook 840 G10'),(N'Lenovo','PC',N'ThinkPad T14'),
                 (N'HP','PNT',N'LaserJet Pro M404dn'),(N'Canon','PNT',N'imageRUNNER 2425'),(N'Cisco','IP',N'IP Phone 8841'),(N'Dell','MON',N'P2422H'),
                 (N'Samsung','MON',N'S24C'),(N'Epson','SCA',N'DS-530'),(N'Microsoft','SOF',N'Office 2021'),(N'HP','TON',N'59A Black')) v(m,t,n)
                 JOIN itam_ref.manufacturer m ON m.name_en=v.m;
                """, c);
            await q.ExecuteNonQueryAsync();
        }
        var officer = new ItamUser(G(1), "demo.assets");
        async Task<int> M(string model) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand("SELECT model_id FROM itam_ref.model WHERE name_en=@n", c); q.Parameters.AddWithValue("@n", model); return (int)(await q.ExecuteScalarAsync())!; }
        async Task<int> Mf(string name) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand("SELECT manufacturer_id FROM itam_ref.manufacturer WHERE name_en=@n", c); q.Parameters.AddWithValue("@n", name); return (int)(await q.ExecuteScalarAsync())!; }
        async Task<int> Loc(string name) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand("SELECT location_id FROM itam_ref.location WHERE name_en=@n", c); q.Parameters.AddWithValue("@n", name); return (int)(await q.ExecuteScalarAsync())!; }
        async Task Reg(string type, string mf, string model, string loc, string? serial, Dictionary<string, string?>? attrs = null, string? tag = null, string? mac = null, string? ip = null, string? osKey = null)
            => await store.Register(officer, new ItamAssetInput(type, "DEMO", await Loc(loc), await Mf(mf), await M(model), null, serial, tag, mac, ip, "New",
                new DateOnly(2026, 3, 15), new DateOnly(2029, 3, 14), null, null, null, null, null, attrs, osKey, null), default);
        await Reg("PC", "Dell", "OptiPlex 7010", "Riyadh IT Warehouse (demo)", "DEMOSN0001", new() { ["ComputerType"] = "Desktop", ["Processor"] = "Intel Core i5", ["MemoryGb"] = "16", ["CapacityGb"] = "512", ["OperatingSystem"] = "Win11" }, "DEMOTAG01", osKey: "DEMO-KEY-11111-22222");
        await Reg("PC", "HP", "EliteBook 840 G10", "Jeddah IT Warehouse (demo)", "DEMOSN0002", new() { ["ComputerType"] = "Notebook", ["MemoryGb"] = "16", ["OperatingSystem"] = "Win11" }, "DEMOTAG02");
        await Reg("PNT", "HP", "LaserJet Pro M404dn", "Abha Branch (demo)", "DEMOPR0001", new() { ["PrinterType"] = "Network", ["PrinterLocation"] = "Reception", ["TonerType"] = "Black" }, ip: "10.20.30.40");
        await Reg("IP", "Cisco", "IP Phone 8841", "Dammam IT Warehouse (demo)", "DEMOIP0001", new() { ["Extension"] = "4321" }, mac: "00:11:22:33:44:55");
        await Reg("IP", "Cisco", "IP Phone 8841", "Jeddah IT Warehouse (demo)", "DEMOIP0002", new() { ["Extension"] = "4322" }, mac: "00:11:22:33:44:56");
        await Reg("MON", "Samsung", "S24C", "Al Kharj Branch (demo)", "DEMOMN0001");
        await Reg("SCA", "Epson", "DS-530", "Dubai Office (demo)", "DEMOSC0001");
        // A PO from Procurement (invented document), partly received.
        var doc = pdf.Start(); doc.Text("Purchase Order 4500000101 (demo document, invented)", ItamPdf.Doc.M, doc.Y, 14); doc.Y -= 20;
        doc.Text("Supplier: Demo Computers Co.   Lines: 5 x Dell Latitude 5440, 5 x Dell P2422H", ItamPdf.Doc.M, doc.Y, 10);
        var po = await store.CreatePo(officer, new ItamPoInput("4500000101", new DateOnly(2026, 10, 12), 1, "DEMO", "Procurement Department", null, null, null,
            [new(1, "PC", await Mf("Dell"), await M("Latitude 5440"), null, "Core i7, 16 GB, 512 GB SSD", 5, 4200m), new(2, "MON", await Mf("Dell"), await M("P2422H"), null, "24 inch", 5, 650m)]),
            new ItamUpload("PO 4500000101.pdf", "application/pdf", doc.Build()), default);
        var poId = (int)po.GetType().GetProperty("id")!.GetValue(po)!;
        async Task<int> Line(int no) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand("SELECT po_line_id FROM itam_data.po_line WHERE po_id=@p AND line_no=@n", c); q.Parameters.AddWithValue("@p", poId); q.Parameters.AddWithValue("@n", no); return (int)(await q.ExecuteScalarAsync())!; }
        int line1 = await Line(1), line2 = await Line(2);
        await store.Receive(officer, poId, new ItamReceiveInput(line1, await Loc("Riyadh IT Warehouse (demo)"), null, null, "First delivery",
            [.. Enumerable.Range(1, 3).Select(i => new ItamDeviceInput($"DEMOLT000{i}", $"DEMOLTAG{i}", null, null, "New", new() { ["ComputerType"] = "Notebook", ["MemoryGb"] = "16", ["OperatingSystem"] = "Win11" }, null, null, null))]), null, default);
        await store.Receive(officer, poId, new ItamReceiveInput(line2, await Loc("Riyadh IT Warehouse (demo)"), null, null, null,
            [.. Enumerable.Range(1, 2).Select(i => new ItamDeviceInput($"DEMOMN010{i}", null, null, null, "New", null, null, null, null))]), null, default);
        Console.WriteLine("IT Assets demo data seeded.");
    }
}
