using System.Data;
using System.Data.Common;
using System.Net;
using System.Text.Json;

namespace Itam.App;

public sealed record ItamAssetInput(string? Type, int? LocationId, int? ManufacturerId, int? ModelId, string? NewModel,
    string? SerialNo, string? ServiceTag, string? MacAddress, string? IpAddress, string? Condition, DateOnly? PurchaseDate,
    int? SupplierId, string? PoNumber, decimal? UnitCost, string? InvoiceNo, string? Notes, Dictionary<string, string?>? Attributes,
    string? OsKey, string? SoftwareKey, string? Version = null, string? Reason = null, string? NewSupplier = null, string? NewSupplierVat = null);

public sealed record ItamAssetFilter(string? Text, string? Type, string? Status, string? Region, int? LocationId, int Page = 1);

public sealed partial class ItamStore
{
    // Values ------------------------------------------------------------------------------------------------------------------
    /// <summary>Serial numbers and service tags compared without spaces, dashes or case.</summary>
    internal static string? Key(string? v) { var k = new string((v ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant(); return k.Length == 0 ? null : k; }
    /// <summary>MAC address as AA:BB:CC:DD:EE:FF.</summary>
    internal static string? Mac(string? v)
    {
        var hex = new string((v ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if ((v ?? "").Trim().Length == 0) return null;
        if (hex.Length != 12) throw new ArgumentException("MAC address: 12 hexadecimal digits, for example AA:BB:CC:DD:EE:FF.");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
    internal static string? Ip(string? v)
    {
        var t = (v ?? "").Trim(); if (t.Length == 0) return null;
        if (!IPAddress.TryParse(t, out var a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || t.Count(ch => ch == '.') != 3) throw new ArgumentException("IP address: an IPv4 address, for example 10.1.2.3.");
        return a.ToString();
    }

    internal sealed record AssetData(string Type, int LocationId, string Region, string LocationKind, int? ManufacturerId, int? ModelId,
        string? Serial, string? ServiceTag, string? Mac, string? Ip, string Condition, DateOnly? PurchaseDate, int? SupplierId,
        string? PoNumber, decimal? UnitCost, string? InvoiceNo, string? Notes, Dictionary<string, string> Attributes);

    /// <summary>Checks a registration or edit against the form 01 rules and the master data.</summary>
    internal async Task<AssetData> Check(DbConnection c, DbTransaction tx, ItamScope s, ItamAssetInput x, string type, int? self, CancellationToken token)
    {
        if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.asset_type WHERE type_code=@t AND active=1", token, ("@t", type)) is null) throw new ArgumentException("Choose the product type.");
        var loc = (await Rows(c, tx, "SELECT region_code,kind,active FROM itam_ref.location WHERE location_id=@l", r => (Region: r.GetString(0), Kind: r.GetString(1), Active: r.GetBoolean(2)), token, ("@l", x.LocationId ?? 0))).SingleOrDefault();
        if (loc.Region is null || !loc.Active) throw new ArgumentException("Choose where the asset is kept (IT warehouse or branch).");
        int? maker = ItamTypeFields.ManufacturerUsed(type) ? x.ManufacturerId : null;
        if (ItamTypeFields.ManufacturerUsed(type) && await Scalar(c, tx, "SELECT 1 FROM itam_ref.manufacturer WHERE manufacturer_id=@m AND active=1", token, ("@m", maker ?? 0)) is null) throw new ArgumentException("Choose the manufacturer.");
        int? model;
        if (!ItamTypeFields.ModelUsed(type)) model = null;
        else if (!string.IsNullOrWhiteSpace(x.NewModel))
        {
            // A model not yet in the list: added on the spot by the person registering (kept in the audit log).
            var name = Text(x.NewModel, "Model", 150);
            model = await Scalar(c, tx, "SELECT model_id FROM itam_ref.model WHERE manufacturer_id=@m AND type_code=@t AND name_en=@n", token, ("@m", x.ManufacturerId), ("@t", type), ("@n", name)) is int existing ? existing
                : Convert.ToInt32(await Scalar(c, tx, "INSERT itam_ref.model(manufacturer_id,type_code,name_en) OUTPUT inserted.model_id VALUES(@m,@t,@n)", token, ("@m", x.ManufacturerId), ("@t", type), ("@n", name)));
            await Audit(c, tx, s.Id, "model", model.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "Add", null, new { manufacturer = x.ManufacturerId, type, name }, "Added while registering an asset", token);
        }
        else
        {
            model = x.ModelId ?? 0;
            if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.model WHERE model_id=@m AND manufacturer_id=@f AND type_code=@t", token, ("@m", model), ("@f", x.ManufacturerId), ("@t", type)) is null)
                throw new ArgumentException("Choose the model (of this manufacturer and type).");
        }
        var serial = ItamTypeFields.SerialUsed(type) ? Optional(x.SerialNo, "Serial number", 100) : null; var tag = ItamTypeFields.ServiceTagUsed(type) ? Optional(x.ServiceTag, "Service tag", 100) : null;
        var mac = ItamTypeFields.MacUsed(type) ? Mac(x.MacAddress) : null; var ip = ItamTypeFields.IpUsed(type) ? Ip(x.IpAddress) : null;
        if (ItamTypeFields.SerialRequired(type) && serial is null) throw new ArgumentException("Serial number is required.");
        if (type == "PC" && tag is null) throw new ArgumentException("Service tag is required for computers.");
        if (ItamTypeFields.MacRequired(type) && mac is null) throw new ArgumentException("MAC address is required for IP phones.");
        async Task Unique(string column, string? value, string label)
        {
            if (value is null) return;
            if (await Scalar(c, tx, $"SELECT asset_no FROM itam_data.asset WHERE {column}=@v AND asset_id<>@i", token, ("@v", value), ("@i", self ?? 0)) is string other)
                throw new ArgumentException($"{label} {value} is already on {other}.");
        }
        await Unique("serial_key", Key(serial), "Serial number"); await Unique("service_tag_key", Key(tag), "Service tag"); await Unique("mac_address", mac, "MAC address");
        var condition = x.Condition is "New" or "Used" ? x.Condition : throw new ArgumentException("Choose the device condition (New or Used).");
        var today = DateOnly.FromDateTime(UtcNow.AddHours(3));
        if (x.PurchaseDate > today) throw new ArgumentException("The purchase date cannot be in the future.");
        var supplier = x.SupplierId;
        if (!string.IsNullOrWhiteSpace(x.NewSupplier))
        {
            // A supplier not yet in the list: added on the spot by the person registering (kept in the audit log).
            var name = Text(x.NewSupplier, "Supplier name", 200);
            var vat = Optional(x.NewSupplierVat, "Supplier VAT number", 20);
            if (vat is not null && (vat.Length != 15 || vat.Any(ch => !char.IsAsciiDigit(ch)))) throw new ArgumentException("Supplier VAT number: 15 digits.");
            if (await Scalar(c, tx, "SELECT supplier_id FROM itam_ref.supplier WHERE name_en=@n", token, ("@n", name)) is int existing) supplier = existing;
            else
            {
                supplier = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_ref.supplier(name_en,vat_no) OUTPUT inserted.supplier_id VALUES(@n,@v)", token, ("@n", name), ("@v", vat)));
                await Audit(c, tx, s.Id, "supplier", name, "Add", null, new { name, vat }, "Added while registering an asset", token);
            }
        }
        else if (supplier is int sup && await Scalar(c, tx, "SELECT 1 FROM itam_ref.supplier WHERE supplier_id=@s", token, ("@s", sup)) is null) throw new ArgumentException("Choose a supplier from the list.");
        if (x.UnitCost is < 0 or > 10_000_000) throw new ArgumentException("Unit cost: a positive amount in SAR.");
        var attributes = new Dictionary<string, string>();
        foreach (var f in ItamTypeFields.For(type))
        {
            var v = (x.Attributes ?? []).TryGetValue(f.Code, out var raw) ? (raw ?? "").Trim() : "";
            if (v.Length == 0) { if (f.Required) throw new ArgumentException($"{f.Label} is required."); continue; }
            if (f.Kind == "number" && (!decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0 || n > 1_000_000)) throw new ArgumentException($"{f.Label}: a number.");
            if (f.Kind == "list" && await Scalar(c, tx, "SELECT 1 FROM itam_ref.value_item WHERE list_code=@l AND item_code=@v", token, ("@l", f.List), ("@v", v)) is null) throw new ArgumentException($"{f.Label}: choose from the list.");
            if (v.Length > 200) throw new ArgumentException($"{f.Label}: up to 200 characters.");
            attributes[f.Code] = v;
        }
        return new(type, x.LocationId!.Value, loc.Region, loc.Kind, maker, model, serial, tag, mac, ip, condition!, x.PurchaseDate, supplier,
            Optional(x.PoNumber, "PO number", 40), x.UnitCost, Optional(x.InvoiceNo, "Invoice number", 60), Optional(x.Notes, "Notes", 2000), attributes);
    }

    /// <summary>Creates the asset with the next Asset No. (short code + the one asset sequence), In Store where it is kept.</summary>
    internal async Task<(int Id, string No)> Create(DbConnection c, DbTransaction tx, Guid actor, AssetData d, int? po, int? poLine, int? receipt, string? osKey, string? softwareKey, string eventText, CancellationToken token)
    {
        var seq = await Next(c, tx, "Asset", token); var no = $"{d.Type}-{seq}";
        var id = Convert.ToInt32(await Scalar(c, tx,
            "INSERT itam_data.asset(asset_no,asset_seq,type_code,status,stock_location,location_id,manufacturer_id,model_id,serial_no,serial_key,service_tag,service_tag_key,mac_address,ip_address," +
            "device_condition,purchase_date,supplier_id,po_id,po_line_id,receipt_id,po_number_text,unit_cost,invoice_no,notes,created_by) OUTPUT inserted.asset_id " +
            "VALUES(@no,@seq,@t,N'InStore',@sl,@l,@m,@mo,@s,@sk,@g,@gk,@mac,@ip,@cd,@pd,@su,@po,@pl,@rc,@pn,@uc,@in,@nt,@by)", token,
            ("@no", no), ("@seq", seq), ("@t", d.Type), ("@sl", d.LocationKind), ("@l", d.LocationId), ("@m", d.ManufacturerId), ("@mo", d.ModelId),
            ("@s", d.Serial), ("@sk", Key(d.Serial)), ("@g", d.ServiceTag), ("@gk", Key(d.ServiceTag)), ("@mac", d.Mac), ("@ip", d.Ip), ("@cd", d.Condition),
            ("@pd", DateParam(d.PurchaseDate)), ("@su", d.SupplierId), ("@po", po), ("@pl", poLine), ("@rc", receipt), ("@pn", d.PoNumber),
            ("@uc", d.UnitCost), ("@in", d.InvoiceNo), ("@nt", d.Notes), ("@by", actor)));
        foreach (var (k, v) in d.Attributes) await Exec(c, tx, "INSERT itam_data.asset_attribute(asset_id,attr_code,value) VALUES(@a,@k,@v)", token, ("@a", id), ("@k", k), ("@v", v));
        await SaveSecrets(c, tx, id, d.Type, osKey, softwareKey, token);
        await Event(c, tx, id, actor, "Registered", eventText, token);
        return (id, no);
    }

    async Task SaveSecrets(DbConnection c, DbTransaction tx, int id, string type, string? osKey, string? softwareKey, CancellationToken token)
    {
        foreach (var (code, value) in new[] { ("OsKey", type == "PC" ? osKey : null), ("SoftwareKey", type == "SOF" ? softwareKey : null) })
        {
            var v = (value ?? "").Trim(); if (v.Length == 0) continue;
            if (v.Length > 200) throw new ArgumentException("Keys: up to 200 characters.");
            await Exec(c, tx, "DELETE itam_data.asset_secret WHERE asset_id=@a AND secret_code=@c; INSERT itam_data.asset_secret(asset_id,secret_code,protected_value) VALUES(@a,@c,@v)", token,
                ("@a", id), ("@c", code), ("@v", secrets.Protect(v)));
        }
    }

    /// <summary>Register one asset (form 01).</summary>
    public async Task<object> Register(ItamUser user, ItamAssetInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.CanRegisterAny, "You cannot register assets.");
        var type = (x.Type ?? "").Trim().ToUpperInvariant();
        var d = await Check(c, tx, s, x, type, null, token);
        Require(s.CanRegister(d.Region), $"You can register assets in your regions only ({string.Join(", ", s.Regions)}).");
        if (!string.IsNullOrWhiteSpace(x.PoNumber) && await Scalar(c, tx, "SELECT 1 FROM itam_data.po WHERE po_number=@p", token, ("@p", x.PoNumber.Trim())) is not null)
            throw new ArgumentException($"PO {x.PoNumber.Trim()} is recorded in IT Assets: receive the device on the PO instead, so the PO stays complete.");
        var (id, no) = await Create(c, tx, s.Id, d, null, null, null, x.OsKey, x.SoftwareKey, "Registered by hand", token);
        await Audit(c, tx, s.Id, "asset", no, "Register", null, new { d.Type, d.LocationId, d.Serial, d.ServiceTag, d.Mac }, x.Reason, token);
        await tx.CommitAsync(token);
        return new { id, assetNo = no };
    }

    /// <summary>Edit asset details (form 02). Status, Asset No. and type never change here.</summary>
    public async Task Update(ItamUser user, int id, ItamAssetInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        var cur = (await Rows(c, tx, "SELECT a.asset_no,a.type_code,g.region_code,a.status,CONVERT(varchar(30),CONVERT(varbinary(8),a.version),2),a.location_id,a.manufacturer_id,a.model_id,a.serial_no,a.service_tag,a.mac_address,a.ip_address," +
            "a.device_condition,a.purchase_date,a.supplier_id,a.po_number_text,a.unit_cost,a.invoice_no,a.notes FROM itam_data.asset a JOIN itam_ref.location g ON g.location_id=a.location_id WHERE a.asset_id=@i",
            r => new { No = r.GetString(0), Type = r.GetString(1), Region = r.GetString(2), Status = r.GetString(3), Version = r.GetString(4),
                In = new ItamAssetInput(r.GetString(1), r.GetInt32(5), I(r, 6), I(r, 7), null, S(r, 8), S(r, 9), S(r, 10), S(r, 11), r.GetString(12),
                    Day(r, 13), I(r, 14), S(r, 15), D(r, 16), S(r, 17), S(r, 18), null, null, null) }, token, ("@i", id))).SingleOrDefault()
            ?? throw new ArgumentException("The asset was not found.");
        if (x.Version != cur.Version) throw new ItamConflict("Someone changed this asset after you opened it. Reload and try again.");
        var attrs = (await Rows(c, tx, "SELECT attr_code,value FROM itam_data.asset_attribute WHERE asset_id=@i", r => (r.GetString(0), r.GetString(1)), token, ("@i", id))).ToDictionary(a => a.Item1, a => (string?)a.Item2);
        ItamAssetInput next;
        if (s.CanEdit(cur.Type, cur.Region)) next = x with { Type = cur.Type };
        else if (s.CanEditPhoneOnly(cur.Type))
        {
            // Voice Administrator: MAC address, IP address and extension of IP phones only.
            var a = new Dictionary<string, string?>(attrs) { ["Extension"] = (x.Attributes ?? []).GetValueOrDefault("Extension") };
            next = cur.In with { MacAddress = x.MacAddress, IpAddress = x.IpAddress, Attributes = a };
        }
        else throw new ItamAccessDenied("You cannot edit this asset.");
        if (next.LocationId != cur.In.LocationId && cur.Status != "InStore") throw new ArgumentException("Where the asset is kept changes only while it is In Store (later: deliveries and stock moves).");
        var d = await Check(c, tx, s, next with { NewModel = next.NewModel }, cur.Type, id, token);
        if (d.Region != cur.Region) Require(s.CanEdit(cur.Type, d.Region), "You can move assets only within your regions.");
        await Exec(c, tx, "UPDATE itam_data.asset SET location_id=@l,stock_location=CASE WHEN status=N'InStore' THEN @sl ELSE stock_location END,manufacturer_id=@m,model_id=@mo,serial_no=@s,serial_key=@sk," +
            "service_tag=@g,service_tag_key=@gk,mac_address=@mac,ip_address=@ip,device_condition=@cd,purchase_date=@pd,supplier_id=@su,po_number_text=@pn,unit_cost=@uc,invoice_no=@in,notes=@nt WHERE asset_id=@i", token,
            ("@i", id), ("@l", d.LocationId), ("@sl", d.LocationKind), ("@m", d.ManufacturerId), ("@mo", d.ModelId), ("@s", d.Serial), ("@sk", Key(d.Serial)),
            ("@g", d.ServiceTag), ("@gk", Key(d.ServiceTag)), ("@mac", d.Mac), ("@ip", d.Ip), ("@cd", d.Condition), ("@pd", DateParam(d.PurchaseDate)),
            ("@su", d.SupplierId), ("@pn", d.PoNumber), ("@uc", d.UnitCost), ("@in", d.InvoiceNo), ("@nt", d.Notes));
        await Exec(c, tx, "DELETE itam_data.asset_attribute WHERE asset_id=@i", token, ("@i", id));
        foreach (var (k, v) in d.Attributes) await Exec(c, tx, "INSERT itam_data.asset_attribute(asset_id,attr_code,value) VALUES(@a,@k,@v)", token, ("@a", id), ("@k", k), ("@v", v));
        if (s.CanEdit(cur.Type, cur.Region)) await SaveSecrets(c, tx, id, cur.Type, x.OsKey, x.SoftwareKey, token);
        // History: which fields changed, old → new (keys never shown).
        var before = cur.In; var changes = new List<string>();
        void Diff(string label, object? a, object? b) { if (!Equals(a?.ToString() ?? "", b?.ToString() ?? "")) changes.Add($"{label}: {a ?? "—"} → {b ?? "—"}"); }
        Diff("Kept at", before.LocationId, d.LocationId); Diff("Model", before.ModelId, d.ModelId); Diff("Serial", before.SerialNo, d.Serial);
        Diff("Service tag", before.ServiceTag, d.ServiceTag); Diff("MAC", before.MacAddress, d.Mac); Diff("IP", before.IpAddress, d.Ip); Diff("Condition", before.Condition, d.Condition);
        Diff("Purchase date", before.PurchaseDate, d.PurchaseDate); Diff("Unit cost", before.UnitCost, d.UnitCost); Diff("Invoice", before.InvoiceNo, d.InvoiceNo);
        foreach (var k in attrs.Keys.Union(d.Attributes.Keys)) Diff(ItamTypeFields.For(cur.Type).FirstOrDefault(f => f.Code == k)?.Label ?? k, attrs.GetValueOrDefault(k), d.Attributes.GetValueOrDefault(k));
        if (!string.IsNullOrWhiteSpace(x.OsKey) || !string.IsNullOrWhiteSpace(x.SoftwareKey)) changes.Add("Key replaced");
        if (changes.Count == 0) { await tx.RollbackAsync(token); return; }
        await Event(c, tx, id, s.Id, "Edited", string.Join("; ", changes), token);
        await Audit(c, tx, s.Id, "asset", cur.No, "Edit", null, new { changes }, x.Reason, token);
        await tx.CommitAsync(token);
    }

    /// <summary>Show an OS or software key: IT Administrator and IT Assets Officer only; every view is logged.</summary>
    public async Task<string> ViewKey(ItamUser user, int id, string code, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(token);
        var s = await Scope(c, tx, user, token);
        Require(s.CanSeeKeys, "Only the IT Administrator and the IT Assets Officer can see keys.");
        var value = await Scalar(c, tx, "SELECT protected_value FROM itam_data.asset_secret WHERE asset_id=@a AND secret_code=@c", token, ("@a", id), ("@c", code)) as string
            ?? throw new ArgumentException("No key is recorded.");
        await Exec(c, tx, "INSERT itam_data.secret_view(asset_id,secret_code,viewer_id) VALUES(@a,@c,@v)", token, ("@a", id), ("@c", code), ("@v", s.Id));
        await Event(c, tx, id, s.Id, "Key viewed", code == "OsKey" ? "OS key" : "Software key", token);
        await tx.CommitAsync(token);
        return secrets.Unprotect(value);
    }

    // Reading -------------------------------------------------------------------------------------------------------------------------
    /// <summary>Which assets the person may see, as SQL on alias a (asset) and g (location).</summary>
    static (string Where, (string, object?)[] Args) Visible(ItamScope s)
    {
        if (s.ViewsAll) return ("1=1", []);
        var parts = new List<string>(); var args = new List<(string, object?)>();
        if (s.Specialist && s.Regions.Length > 0) { parts.Add("g.region_code IN(" + string.Join(",", s.Regions.Select((_, i) => "@vr" + i)) + ")"); args.AddRange(s.Regions.Select((r, i) => ("@vr" + i, (object?)r))); }
        if (s.SysAdmin) parts.Add("a.type_code=N'PC'");
        if (s.Voice) parts.Add("a.type_code=N'IP'");
        if (parts.Count == 0) throw new ItamAccessDenied("You have no IT role that shows assets.");
        return ("(" + string.Join(" OR ", parts) + ")", [.. args]);
    }

    public async Task<object> Assets(ItamUser user, ItamAssetFilter f, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var (where, args) = Visible(s);
        var parts = new List<string> { where }; var a = new List<(string, object?)>(args);
        if (!string.IsNullOrWhiteSpace(f.Text))
        {
            var t = f.Text.Trim(); if (t.Length > 100) t = t[..100];
            parts.Add("(a.asset_no=@no OR a.serial_key LIKE @k OR a.service_tag_key LIKE @k OR a.mac_address LIKE @lk OR mo.name_en LIKE @lk OR EXISTS(SELECT 1 FROM itam_data.asset_attribute x WHERE x.asset_id=a.asset_id AND x.attr_code IN(N'ComputerName',N'Extension',N'DeviceName',N'Software',N'TonerCode') AND x.value LIKE @lk) OR a.po_number_text=@no OR p.po_number=@no)");
            a.Add(("@no", t)); a.Add(("@k", "%" + (Key(t) ?? "~") + "%")); a.Add(("@lk", "%" + t.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%"));
        }
        if (!string.IsNullOrWhiteSpace(f.Type)) { parts.Add("a.type_code=@t"); a.Add(("@t", f.Type)); }
        if (!string.IsNullOrWhiteSpace(f.Status)) { parts.Add("a.status=@st"); a.Add(("@st", f.Status)); }
        if (!string.IsNullOrWhiteSpace(f.Region)) { parts.Add("g.region_code=@rg"); a.Add(("@rg", f.Region)); }
        if (f.LocationId is int l) { parts.Add("a.location_id=@l"); a.Add(("@l", l)); }
        var page = Math.Clamp(f.Page, 1, 10_000);
        var rows = await Rows(c, null,
            "SELECT a.asset_id,a.asset_no,a.type_code,t.name_en,m.name_en,mo.name_en,a.serial_no,a.status,g.name_en,g.region_code,COALESCE(p.po_number,a.po_number_text),a.service_tag," +
            "(SELECT TOP 1 value FROM itam_data.asset_attribute x WHERE x.asset_id=a.asset_id AND x.attr_code=N'ComputerName')," +
            "(SELECT TOP 1 value FROM itam_data.asset_attribute x WHERE x.asset_id=a.asset_id AND x.attr_code IN(N'Software',N'TonerCode')) " +
            "FROM itam_data.asset a JOIN itam_ref.location g ON g.location_id=a.location_id JOIN itam_ref.asset_type t ON t.type_code=a.type_code " +
            "LEFT JOIN itam_ref.manufacturer m ON m.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id LEFT JOIN itam_data.po p ON p.po_id=a.po_id " +
            $"WHERE {string.Join(" AND ", parts)} ORDER BY a.asset_seq DESC OFFSET @skip ROWS FETCH NEXT 51 ROWS ONLY",
            r => new { id = r.GetInt32(0), assetNo = r.GetString(1), type = r.GetString(2), typeName = r.GetString(3), manufacturer = S(r, 4), model = S(r, 5) ?? S(r, 13),
                serial = S(r, 6), status = r.GetString(7), statusLabel = ItamStatuses.Label(r.GetString(7)), keptAt = r.GetString(8), region = r.GetString(9), po = S(r, 10), serviceTag = S(r, 11), computerName = S(r, 12) },
            token, [.. a, ("@skip", (page - 1) * 50)]);
        return new { page, hasMore = rows.Count > 50, items = rows.Take(50) };
    }

    public async Task<object> Asset(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        var a = (await Rows(c, null,
            "SELECT a.asset_no,a.type_code,t.name_en,a.status,a.stock_location,a.location_id,g.name_en,g.region_code,a.manufacturer_id,m.name_en,a.model_id,mo.name_en," +
            "a.serial_no,a.service_tag,a.mac_address,a.ip_address,a.device_condition,a.purchase_date,a.supplier_id,su.name_en,a.po_id,p.po_number,a.po_number_text,a.po_line_id," +
            "a.unit_cost,a.invoice_no,a.notes,a.created_by,a.created_at_utc,CONVERT(varchar(30),CONVERT(varbinary(8),a.version),2),r.received_at_utc,r.received_by,pl.line_no " +
            "FROM itam_data.asset a JOIN itam_ref.asset_type t ON t.type_code=a.type_code JOIN itam_ref.location g ON g.location_id=a.location_id " +
            "LEFT JOIN itam_ref.manufacturer m ON m.manufacturer_id=a.manufacturer_id LEFT JOIN itam_ref.model mo ON mo.model_id=a.model_id LEFT JOIN itam_ref.supplier su ON su.supplier_id=a.supplier_id " +
            "LEFT JOIN itam_data.po p ON p.po_id=a.po_id LEFT JOIN itam_data.po_receipt r ON r.receipt_id=a.receipt_id LEFT JOIN itam_data.po_line pl ON pl.po_line_id=a.po_line_id WHERE a.asset_id=@i",
            r => new
            {
                id, assetNo = r.GetString(0), type = r.GetString(1), typeName = r.GetString(2), status = r.GetString(3), statusLabel = ItamStatuses.Label(r.GetString(3)),
                stockLocation = r.GetString(4), locationId = r.GetInt32(5), keptAt = r.GetString(6), region = r.GetString(7), manufacturerId = I(r, 8), manufacturer = S(r, 9), modelId = I(r, 10), model = S(r, 11),
                serial = S(r, 12), serviceTag = S(r, 13), mac = S(r, 14), ip = S(r, 15), condition = r.GetString(16), purchaseDate = Day(r, 17), supplierId = I(r, 18), supplier = S(r, 19),
                poId = I(r, 20), poNumber = S(r, 21) ?? S(r, 22), poLine = I(r, 32), unitCost = D(r, 24), invoiceNo = S(r, 25), notes = S(r, 26), createdBy = r.GetGuid(27), createdAt = Utc(r, 28), version = r.GetString(29),
                receivedAt = r.IsDBNull(30) ? (DateTime?)null : Utc(r, 30), receivedBy = r.IsDBNull(31) ? (Guid?)null : r.GetGuid(31)
            }, token, ("@i", id))).SingleOrDefault() ?? throw new ArgumentException("The asset was not found.");
        Require(s.CanView(a.type, a.region), "You cannot see this asset.");
        var names = await Names(c, null, token);
        string N(Guid? g) => g is Guid x && names.TryGetValue(x, out var n) ? n : "";
        var attributes = (await Rows(c, null, "SELECT attr_code,value FROM itam_data.asset_attribute WHERE asset_id=@i", r => (r.GetString(0), r.GetString(1)), token, ("@i", id))).ToDictionary(x => x.Item1, x => x.Item2);
        var keys = await Rows(c, null, "SELECT secret_code FROM itam_data.asset_secret WHERE asset_id=@i", r => r.GetString(0), token, ("@i", id));
        var history = await Rows(c, null, "SELECT at_utc,actor_id,kind,detail FROM itam_data.asset_event WHERE asset_id=@i ORDER BY event_id DESC", r => new { at = Utc(r, 0), who = N(r.GetGuid(1)), kind = r.GetString(2), detail = S(r, 3) }, token, ("@i", id));
        return new
        {
            asset = a, createdByName = N(a.createdBy), receivedByName = N(a.receivedBy), attributes, fields = ItamTypeFields.For(a.type), keys, history,
            canEdit = s.CanEdit(a.type, a.region), phoneOnly = !s.CanEdit(a.type, a.region) && s.CanEditPhoneOnly(a.type), canSeeKeys = s.CanSeeKeys
        };
    }
}
