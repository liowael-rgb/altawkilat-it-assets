using Itam.App;
using Microsoft.Data.SqlClient;

// Invented demo data, entered as an administrator would, then assets and a PO through the real IT Assets code.
static class DemoSeed
{
    static Guid G(int n) => new($"00000000-0000-0000-0000-{n:000000000000}");

    public static async Task Run(string cs, ItamStore store, ItamPdf pdf)
    {
        // Invented directory details (no real people): mobile, branch, company and HR code as printed on the delivery note.
        await store.SyncEmployees([.. DemoPeople.All.Select((p, i) => new ItamEmployee(p.Id, p.Account, p.Name, p.Account + "@example.invalid", p.Role, i is >= 9 and <= 11 ? "Sales (demo)" : "IT (demo)", p.Id == G(2) ? null : G(2), true,
            $"050000{i + 1:0000}", i is >= 9 and <= 11 ? "Abha Branch (demo)" : "Head Office (demo)", "ALTAWKILAT (demo)", $"DEMO-{i + 1:000}", i is >= 9 and <= 11 ? "South" : null, $"{70000 + i + 1}"))], default);
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
                 ('00000000-0000-0000-0000-000000000008','SupportSpecialist','South','00000000-0000-0000-0000-000000000002'),
                 ('00000000-0000-0000-0000-000000000013','InkOfficer','South','00000000-0000-0000-0000-000000000002'),
                 -- Several roles (owner's structure): the IT Manager is the System Administrator; the Ink Officer and the IT Assets
                 -- Officer are IT Support Specialists.
                 ('00000000-0000-0000-0000-000000000002','SystemAdministrator',NULL,'00000000-0000-0000-0000-000000000002'),
                 ('00000000-0000-0000-0000-000000000013','SupportSpecialist','South','00000000-0000-0000-0000-000000000002'),
                 ('00000000-0000-0000-0000-000000000001','SupportSpecialist','Central','00000000-0000-0000-0000-000000000002');
                INSERT itam_ref.location(name_en,kind,region_code,city) VALUES
                 (N'Riyadh IT Warehouse (demo)','ITWarehouse','Central',N'Riyadh'),(N'Jeddah IT Warehouse (demo)','ITWarehouse','West',N'Jeddah'),
                 (N'Dammam IT Warehouse (demo)','ITWarehouse','East',N'Dammam'),(N'Abha Branch (demo)','Branch','South',N'Abha'),
                 (N'Al Kharj Branch (demo)','Branch','Central',N'Al Kharj'),(N'Dubai Office (demo)','Branch','UAE',N'Dubai');
                INSERT itam_ref.supplier(name_en,name_ar,vat_no,cr_no,contact_name,phone,email,address) VALUES(N'Demo Computers Co.',N'شركة الحاسبات التجريبية','300000000000003','1010000001',N'Demo Sales Desk','0110000001','sales@computers.example.invalid',N'Riyadh'),
                 (N'Demo Network Supplies',NULL,NULL,NULL,NULL,NULL,NULL,NULL),(N'Demo Office Equipment',NULL,NULL,NULL,NULL,NULL,NULL,NULL),
                 (N'Demo Toner Trading (A)',N'مؤسسة الأحبار التجريبية (أ)','300000000000011','4030000002',N'Demo Toner Desk','0120000002','orders@toner-a.example.invalid',N'Jeddah'),(N'Demo Toner Trading (B)',N'مؤسسة الأحبار التجريبية (ب)','300000000000029',NULL,N'Demo Contact B','0130000003',NULL,N'Dammam');
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
            => await store.Register(officer, new ItamAssetInput(type, await Loc(loc), await Mf(mf), await M(model), null, serial, tag, mac, ip, "New",
                new DateOnly(2026, 3, 15), null, null, null, null, null, attrs, osKey, null), default);
        await Reg("PC", "Dell", "OptiPlex 7010", "Riyadh IT Warehouse (demo)", "DEMOSN0001", new() { ["ComputerType"] = "Desktop", ["Processor"] = "Intel Core i5", ["MemoryGb"] = "16", ["CapacityGb"] = "512", ["OperatingSystem"] = "Win11" }, "DEMOTAG01", osKey: "DEMO-KEY-11111-22222");
        await Reg("PC", "HP", "EliteBook 840 G10", "Jeddah IT Warehouse (demo)", "DEMOSN0002", new() { ["ComputerType"] = "Notebook", ["Processor"] = "Intel Core i7", ["MemoryGb"] = "16", ["CapacityGb"] = "512", ["OperatingSystem"] = "Win11" }, "DEMOTAG02");
        await Reg("PNT", "HP", "LaserJet Pro M404dn", "Abha Branch (demo)", "DEMOPR0001", new() { ["PrinterType"] = "Network", ["PrinterLocation"] = "Reception", ["TonerType"] = "Black" }, ip: "10.20.30.40");
        await Reg("IP", "Cisco", "IP Phone 8841", "Dammam IT Warehouse (demo)", "DEMOIP0001", new() { ["Extension"] = "4321" }, mac: "00:11:22:33:44:55");
        await Reg("IP", "Cisco", "IP Phone 8841", "Jeddah IT Warehouse (demo)", "DEMOIP0002", new() { ["Extension"] = "4322" }, mac: "00:11:22:33:44:56");
        await Reg("MON", "Samsung", "S24C", "Al Kharj Branch (demo)", "DEMOMN0001");
        await Reg("SCA", "Epson", "DS-530", "Dubai Office (demo)", "DEMOSC0001");
        // A PO from Procurement (invented document), partly received.
        var doc = pdf.Start(); doc.Text("Purchase Order 4500000101 (demo document, invented)", ItamPdf.Doc.M, doc.Y, 14); doc.Y -= 20;
        doc.Text("Supplier: Demo Computers Co.   Lines: 5 x Dell Latitude 5440, 5 x Dell P2422H", ItamPdf.Doc.M, doc.Y, 10);
        var po = await store.CreatePo(officer, new ItamPoInput("4500000101", new DateOnly(2026, 10, 12), 1, "Procurement Department", null, null, null,
            [new(1, "PC", await Mf("Dell"), await M("Latitude 5440"), null, "Core i7, 16 GB, 512 GB SSD", 5, 4200m), new(2, "MON", await Mf("Dell"), await M("P2422H"), null, "24 inch", 5, 650m)]),
            new ItamUpload("PO 4500000101.pdf", "application/pdf", doc.Build()), default);
        var poId = (int)po.GetType().GetProperty("id")!.GetValue(po)!;
        async Task<int> Line(int no) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand("SELECT po_line_id FROM itam_data.po_line WHERE po_id=@p AND line_no=@n", c); q.Parameters.AddWithValue("@p", poId); q.Parameters.AddWithValue("@n", no); return (int)(await q.ExecuteScalarAsync())!; }
        int line1 = await Line(1), line2 = await Line(2);
        await store.Receive(officer, poId, new ItamReceiveInput(line1, await Loc("Riyadh IT Warehouse (demo)"), null, null, "First delivery",
            [.. Enumerable.Range(1, 3).Select(i => new ItamDeviceInput($"DEMOLT000{i}", $"DEMOLTAG{i}", null, null, "New", new() { ["ComputerType"] = "Notebook", ["Processor"] = "Intel Core i7", ["MemoryGb"] = "16", ["CapacityGb"] = "512", ["OperatingSystem"] = "Win11" }, null, null, null))]), null, default);
        await store.Receive(officer, poId, new ItamReceiveInput(line2, await Loc("Riyadh IT Warehouse (demo)"), null, null, null,
            [.. Enumerable.Range(1, 2).Select(i => new ItamDeviceInput($"DEMOMN010{i}", null, null, null, "New", null, null, null, null))]), null, default);
        // Handover (step 2): deliveries in every state, one overdue for its signed copy, two returns with their clean-up tasks.
        async Task<int> AssetBy(string column, string value) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand($"SELECT asset_id FROM itam_data.asset WHERE {column}=@v", c); q.Parameters.AddWithValue("@v", value); return (int)(await q.ExecuteScalarAsync())!; }
        static int DeliveryId(object result) => (int)result.GetType().GetProperty("id")!.GetValue(result)!;
        async Task Sql(string sql) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand(sql, c); await q.ExecuteNonQueryAsync(); }
        var scan = pdf.Start(); scan.Text("Delivery note signed by the employee (demo scan, invented)", ItamPdf.Doc.M, scan.Y, 14); var scanPdf = scan.Build();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var d1 = DeliveryId(await store.Deliver(officer, await AssetBy("service_tag", "DEMOLTAG1"), new(G(10), today.AddDays(-6), "Personal", "ATWK-DEMO-001", null), default));
        await store.UploadSigned(officer, d1, new ItamUpload("signed.pdf", "application/pdf", scanPdf), default);
        var d2 = DeliveryId(await store.Deliver(officer, await AssetBy("service_tag", "DEMOLTAG2"), new(G(11), today.AddDays(-5), "Personal", "ATWK-DEMO-002", null), default));
        await Sql($"UPDATE itam_data.delivery SET issued_at_utc=DATEADD(day,-5,SYSUTCDATETIME()) WHERE delivery_id={d2}");
        await store.Deliver(officer, await AssetBy("serial_no", "DEMOMN0101"), new(G(10), today, "Personal", null, "Second screen"), default);
        var d4 = DeliveryId(await store.Deliver(officer, await AssetBy("mac_address", "00:11:22:33:44:55"), new(G(12), today.AddDays(-20), "Department", null, "Reception desk"), default));
        await store.UploadSigned(officer, d4, new ItamUpload("signed.pdf", "application/pdf", scanPdf), default);
        await store.ReturnAsset(officer, d4, new(DateTime.UtcNow.AddDays(-2), "Working", await Loc("Dammam IT Warehouse (demo)"), "Department moved"), default);
        var d5 = DeliveryId(await store.Deliver(officer, await AssetBy("service_tag", "DEMOTAG01"), new(G(11), today.AddDays(-40), "Personal", "ATWK-DEMO-009", null), default));
        await store.UploadSigned(officer, d5, new ItamUpload("signed.pdf", "application/pdf", scanPdf), default);
        await store.ReturnAsset(officer, d5, new(DateTime.UtcNow.AddDays(-1), "Faulty", await Loc("Riyadh IT Warehouse (demo)"), "Does not start"), default);
        await store.Assess(officer, d5, new("Motherboard failure; repair costs more than the device is worth.", "Dispose"), null, default);

        // Ink and invoices (step 3a): a department printer with its toner code; requests in every state; an order, a toner
        // invoice and a payment pack; a device invoice for the PO. Managers are the Demo IT Manager (from the directory copy).
        var printer = await AssetBy("serial_no", "DEMOPR0001");
        await Sql($"INSERT itam_data.asset_attribute(asset_id,attr_code,value) VALUES({printer},N'TonerCode',N'HP 59A CF259A')");
        await store.Deliver(officer, printer, new(G(12), today.AddDays(-90), "Department", null, "Reception printer"), default);
        ItamUser Who(int n) => new(G(n), DemoPeople.All[n - 1].Account);
        async Task<int> One(string sql) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand(sql, c); return (int)(await q.ExecuteScalarAsync())!; }
        var supplierA = await One("SELECT supplier_id FROM itam_ref.supplier WHERE name_en=N'Demo Toner Trading (A)'");
        var k1 = DeliveryId(await store.CreateInkRequest(Who(10), new(printer, null, 2, null, "Black is empty"), default));
        await store.SendInkForApproval(Who(13), k1, new(G(2)), default); await store.DecideInk(Who(2), k1, new(true, null), default);
        var k2 = DeliveryId(await store.CreateInkRequest(Who(11), new(printer, null, 1, null, null), default));
        await store.SendInkForApproval(Who(13), k2, new(G(2)), default); await store.DecideInk(Who(2), k2, new(true, null), default);
        var k3 = DeliveryId(await store.CreateInkRequest(Who(12), new(printer, null, 1, null, null), default));
        await store.SendInkForApproval(Who(13), k3, new(G(2)), default); await store.DecideInk(Who(2), k3, new(true, null), default);
        var ord = DeliveryId(await store.CreateOrder(Who(13), new(supplierA, [k1, k2], "Deliver to Abha branch"), default));
        var invDoc = pdf.Start(); invDoc.Text("Tax invoice T-5001 (demo, invented) - Demo Toner Trading (A)", ItamPdf.Doc.M, invDoc.Y, 13); invDoc.Y -= 18;
        invDoc.Text("HP 59A CF259A Black x 4 @ 290.00 SAR", ItamPdf.Doc.M, invDoc.Y, 11);
        var inv = DeliveryId(await store.CreateInvoice(Who(13), new(supplierA, "T-5001", today.AddDays(-3), "Toners", ord, null, null, null, [new("HP 59A CF259A Black", 4, 290m)]), new ItamUpload("T-5001.pdf", "application/pdf", invDoc.Build()), default));
        var line = await One($"SELECT line_id FROM itam_data.invoice_line WHERE invoice_id={inv}");
        await store.DeliverInk(Who(13), k1, new(today, "Invoice", line), default);
        await store.DeliverInk(Who(13), k3, new(today, "Shelf", null), default);
        await store.CreateInkRequest(Who(10), new(printer, null, 1, null, "Second cartridge for the week"), default);
        var k5 = DeliveryId(await store.CreateInkRequest(Who(11), new(printer, null, 3, null, null), default));
        await store.SendInkForApproval(Who(13), k5, new(G(2)), default); await store.DecideInk(Who(2), k5, new(false, "Three is too many; ask for one at a time."), default);
        var k6 = DeliveryId(await store.CreateInkRequest(Who(12), new(printer, null, 1, null, "Cyan for the colour printer"), default));
        await store.SendInkForApproval(Who(13), k6, new(G(2)), default);
        var pay1 = DeliveryId(await store.CreatePack(Who(13), new(supplierA, "Toners", [inv]), default));
        await store.PackDates(Who(13), pay1, new(today, null, null), default);
        // A printer not in IT Assets: the employee types its serial number and model; the region comes from the AD office (Abha, South).
        var canon = await One("SELECT model_id FROM itam_ref.model WHERE name_en=N'imageRUNNER 2425'");
        await store.CreateInkRequest(Who(11), new(null, null, 1, "C-EXV33", "Printer in the accounts room has no IT label", "DEMO-CN-7781", canon), default);
        var devDoc = pdf.Start(); devDoc.Text("Tax invoice D-7001 (demo, invented) - Demo Computers Co.", ItamPdf.Doc.M, devDoc.Y, 13);
        var rec = await One($"SELECT TOP 1 receipt_id FROM itam_data.po_receipt WHERE po_id={poId} ORDER BY receipt_id");
        await store.CreateInvoice(officer, new(1, "D-7001", today.AddDays(-5), "Devices", null, poId, [rec], null, [new("Dell Latitude 5440", 3, 4200m)]), new ItamUpload("D-7001.pdf", "application/pdf", devDoc.Build()), default);
        // Maintenance orders (invented): one repaired by the supplier and closed, one waiting for the IT Manager; an IT repair.
        var quoteDoc = pdf.Start(); quoteDoc.Text("Quotation Q-1001 (demo, invented) - Demo Computers Co.: replace the keyboard, 250 SAR", ItamPdf.Doc.M, quoteDoc.Y, 12);
        var quote = new ItamUpload("Q-1001.pdf", "application/pdf", quoteDoc.Build());
        var mo1 = DeliveryId(await store.CreateMaintenanceOrder(officer, new("PC-4817", 1, "Q-1001", 250m, "Some keyboard keys do not work: replace the keyboard", today.AddDays(5)), quote, default));
        await store.DecideMaintenance(Who(2), mo1, new(true, null), default);
        await store.DispatchMaintenance(officer, mo1, new(today), default);
        await store.ReturnMaintenance(officer, mo1, new(today, null, "Repaired", "Keyboard replaced and tested", 250m, "R-2201"), default);
        await store.CreateMaintenanceOrder(officer, new("MON-4820", 1, "Q-1002", 380m, "No picture: replace the panel under warranty", today.AddDays(10)), quote, default);
        await store.ItRepair(officer, await One("SELECT asset_id FROM itam_data.asset WHERE asset_no=N'PC-4816'"), new("Windows profile repaired and Office reinstalled"), default);
        // Device and accessory requests (invented), one at each stage.
        var r1 = DeliveryId(await store.CreateRequest(Who(10), new(null, "New", null, "A laptop for visits to the branches"), null, default));
        await store.DecideRequest(Who(2), r1, new(true, null), default);
        await store.ReviewRequest(officer, r1, new("Stock", await One("SELECT asset_id FROM itam_data.asset WHERE asset_no=N'PC-4818'"), G(5), null), default);
        var taDoc = pdf.Start(); taDoc.Text("Technical assessment (demo, invented): PC-4817 battery and keyboard worn out", ItamPdf.Doc.M, taDoc.Y, 12);
        var r2 = DeliveryId(await store.CreateRequest(Who(11), new(null, "Replacement", await One("SELECT asset_id FROM itam_data.asset WHERE asset_no=N'PC-4817'"), "The battery lasts one hour and the keyboard keeps failing"),
            new ItamUpload("assessment.pdf", "application/pdf", taDoc.Build()), default));
        await store.DecideRequest(Who(2), r2, new(true, null), default);
        await store.ReviewRequest(officer, r2, new("NewDevice", null, null, null), default);
        await store.SubmitCost(officer, r2, new(1, null, [new("Dell Latitude 5440", "Core i7, 16 GB, 512 GB SSD", 1, 4200m), new("Laptop bag", null, 1, 120m)]), quote, default);
        var r3 = DeliveryId(await store.CreateRequest(Who(12), new(null, "New", null, "A wireless mouse and keyboard for the reception desk"), null, default));
        await store.DecideRequest(Who(2), r3, new(true, null), default);
        await store.ReviewRequest(officer, r3, new("Accessory", null, G(4), null), default);
        await store.SubmitCost(Who(4), r3, new(null, "Abha Computer Shop (demo)", [new("Wireless mouse and keyboard set", null, 1, 95m)]), null, default);
        await store.DecideCost(Who(2), r3, new(true, null), default);
        await store.CreateRequest(Who(10), new(null, "New", null, "A second monitor for reports"), null, default);
        // PR-2's cost approval close to its 3-day deadline, to show the countdown (about 10 hours left).
        await using (var late = new SqlConnection(cs)) { await late.OpenAsync(); using var u = new SqlCommand("UPDATE itam_data.request SET priced_at_utc=DATEADD(hour,-62,SYSUTCDATETIME()) WHERE request_id=@i", late); u.Parameters.AddWithValue("@i", r2); await u.ExecuteNonQueryAsync(); }
        // Movements (invented): a branch transfer waiting for the Team Leader, a warehouse transfer in transit, an exit pass ready.
        var mvToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        async Task<int> AssetId(string no) => await One($"SELECT asset_id FROM itam_data.asset WHERE asset_no=N'{no}'");
        await store.CreateMovement(Who(10), new("Branch", null, [await AssetId("MON-4819")], "Move to another branch", null, null, await Loc("Abha Branch (demo)"), await Loc("Al Kharj Branch (demo)"), null,
            mvToday, mvToday.AddDays(3), "Employee", null, null, null, G(11), null), default);
        await Reg("MON", "Dell", "P2422H", "Riyadh IT Warehouse (demo)", "DEMOWT001"); await Reg("MON", "Dell", "P2422H", "Riyadh IT Warehouse (demo)", "DEMOWT002");
        var wtAssets = new[] { await One("SELECT asset_id FROM itam_data.asset WHERE serial_no=N'DEMOWT001'"), await One("SELECT asset_id FROM itam_data.asset WHERE serial_no=N'DEMOWT002'") };
        var wtId = DeliveryId(await store.CreateMovement(officer, new("Warehouse", null, wtAssets, "Monitors for the Jeddah branches", null, null, await Loc("Riyadh IT Warehouse (demo)"), await Loc("Jeddah IT Warehouse (demo)"), null,
            mvToday, mvToday.AddDays(2), "Other", "Demo Transport Co.", "0550000099", null, G(4), null), default));
        await store.DispatchMovement(officer, wtId, new(null), default);
        var gpExit = DeliveryId(await store.CreateMovement(Who(11), new("Exit", null, [await AssetId("PC-4817")], "Work from home", "Two days of remote work", "Returnable", await Loc("Abha Branch (demo)"), null, "Home",
            mvToday, mvToday.AddDays(2), "Employee", null, null, null, null, null), default));
        await store.DecideMovement(Who(3), gpExit, new(true, null, null), default);
        Console.WriteLine("IT Assets demo data seeded.");
    }
}
