using System.Data;

namespace Itam.App;

public sealed record ItamSupplierInput(string? Name, string? VatNo, string? CrNo, string? ContactName, string? Phone, string? Email, string? Address, string? Notes, bool Active = true);

/// <summary>Supplier module (owner, 22 October 2026): suppliers with their contact details, invoices, payment packs, toner
/// orders and POs. The Ink Officer, the IT Assets Officer and the IT Administrator add and edit suppliers; invoices of one
/// supplier are picked into a payment pack from the supplier's page.</summary>
public sealed partial class ItamStore
{
    public async Task<object> Suppliers(ItamUser user, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(CanSeeInvoices(s) || s.SuppliersManage, "You cannot see suppliers.");
        var list = await Rows(c, null, "SELECT su.supplier_id,su.name_en,su.vat_no,su.contact_name,su.phone,su.email,su.active," +
            "(SELECT COUNT(*) FROM itam_data.invoice i WHERE i.supplier_id=su.supplier_id)," +
            "(SELECT COUNT(*) FROM itam_data.invoice i WHERE i.supplier_id=su.supplier_id AND i.pack_id IS NULL)," +
            "(SELECT SUM(i.total) FROM itam_data.invoice i WHERE i.supplier_id=su.supplier_id AND i.pack_id IS NULL)," +
            "(SELECT COUNT(*) FROM itam_data.payment_pack pk WHERE pk.supplier_id=su.supplier_id AND pk.paid_on IS NULL) " +
            "FROM itam_ref.supplier su ORDER BY su.active DESC,su.name_en",
            r => new { id = r.GetInt32(0), name = r.GetString(1), vat = S(r, 2), contact = S(r, 3), phone = S(r, 4), email = S(r, 5), active = r.GetBoolean(6),
                invoices = r.GetInt32(7), waiting = r.GetInt32(8), waitingTotal = r.IsDBNull(9) ? 0 : r.GetDecimal(9), unpaidPacks = r.GetInt32(10) }, token);
        return new { suppliers = list, canManage = s.SuppliersManage };
    }

    /// <summary>One supplier: details, invoices (those waiting for a pack can be picked), payment packs, toner orders and POs.</summary>
    public async Task<object> Supplier(ItamUser user, int id, CancellationToken token)
    {
        await using var c = await Open(token);
        var s = await Scope(c, null, user, token);
        Require(CanSeeInvoices(s) || s.SuppliersManage, "You cannot see suppliers.");
        var su = (await Rows(c, null, "SELECT su.supplier_id,su.name_en,su.vat_no,su.cr_no,su.contact_name,su.phone,su.email,su.address,su.notes,su.active,su.created_at_utc,e.display_name " +
            "FROM itam_ref.supplier su LEFT JOIN itam_access.employee e ON e.employee_id=su.created_by WHERE su.supplier_id=@s",
            r => new { id = r.GetInt32(0), name = r.GetString(1), vat = S(r, 2), cr = S(r, 3), contact = S(r, 4), phone = S(r, 5), email = S(r, 6), address = S(r, 7), notes = S(r, 8),
                active = r.GetBoolean(9), createdAt = Utc(r, 10), createdBy = S(r, 11) }, token, ("@s", id))).SingleOrDefault() ?? throw new ArgumentException("The supplier was not found.");
        var invoices = CanSeeInvoices(s) ? await Rows(c, null, "SELECT i.invoice_id,i.invoice_no,i.invoice_date,i.category,i.total,o.order_no,p.po_number,pk.pack_no,i.pack_id,pk.paid_on,pk.sent_on " +
            "FROM itam_data.invoice i LEFT JOIN itam_data.supplier_order o ON o.order_id=i.order_id LEFT JOIN itam_data.po p ON p.po_id=i.po_id LEFT JOIN itam_data.payment_pack pk ON pk.pack_id=i.pack_id " +
            "WHERE i.supplier_id=@s ORDER BY i.invoice_date DESC,i.invoice_id DESC",
            r => new { id = r.GetInt32(0), no = r.GetString(1), date = Day(r, 2), category = r.GetString(3), categoryLabel = CategoryLabel(r.GetString(3)), total = r.GetDecimal(4),
                order = S(r, 5), po = S(r, 6), pack = S(r, 7), packId = I(r, 8), paidOn = Day(r, 9), sentOn = Day(r, 10), canPack = CanInvoice(s, r.GetString(3)) }, token, ("@s", id)) : [];
        var packs = CanSeeInvoices(s) ? await Rows(c, null, "SELECT pk.pack_id,pk.pack_no,pk.category,pk.created_at_utc,pk.sent_on,pk.paid_on,(SELECT COUNT(*) FROM itam_data.invoice i WHERE i.pack_id=pk.pack_id)," +
            "(SELECT SUM(total) FROM itam_data.invoice i WHERE i.pack_id=pk.pack_id) FROM itam_data.payment_pack pk WHERE pk.supplier_id=@s ORDER BY pk.pack_id DESC",
            r => new { id = r.GetInt32(0), no = r.GetString(1), category = CategoryLabel(r.GetString(2)), at = Utc(r, 3), sentOn = Day(r, 4), paidOn = Day(r, 5), invoices = r.GetInt32(6), total = r.IsDBNull(7) ? 0 : r.GetDecimal(7) }, token, ("@s", id)) : [];
        var orders = await Rows(c, null, "SELECT o.order_id,o.order_no,o.region_code,o.created_at_utc,(SELECT COUNT(*) FROM itam_data.ink_request r WHERE r.order_id=o.order_id)," +
            "(SELECT COUNT(*) FROM itam_data.ink_request r WHERE r.order_id=o.order_id AND r.status=N'Delivered') FROM itam_data.supplier_order o WHERE o.supplier_id=@s ORDER BY o.order_id DESC",
            r => new { id = r.GetInt32(0), no = r.GetString(1), region = r.GetString(2), at = Utc(r, 3), requests = r.GetInt32(4), delivered = r.GetInt32(5) }, token, ("@s", id));
        var pos = s.PoView ? await Rows(c, null, "SELECT po_id,po_number,po_date FROM itam_data.po WHERE supplier_id=@s ORDER BY po_date DESC", r => new { id = r.GetInt32(0), no = r.GetString(1), date = Day(r, 2) }, token, ("@s", id)) : [];
        return new { supplier = su, invoices, packs, orders = orders.Where(o => s.CanInk(o.region) || s.ViewsAll || s.PoView).ToList(), pos, canManage = s.SuppliersManage, canPack = CanSeeInvoices(s) };
    }

    /// <summary>Add a supplier, or change one (<paramref name="id"/>). The name is unique; VAT number 15 digits (ZATCA); every
    /// change is in the audit log.</summary>
    public async Task<object> SaveSupplier(ItamUser user, int? id, ItamSupplierInput x, CancellationToken token)
    {
        await using var c = await Open(token);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var s = await Scope(c, tx, user, token);
        Require(s.SuppliersManage, "Only the Ink Officer, the IT Assets Officer and the IT Administrator add or change suppliers.");
        var name = Text(x.Name, "Supplier name", 200);
        var vat = Optional(x.VatNo, "VAT number", 20);
        if (vat is not null && (vat.Length != 15 || vat.Any(ch => !char.IsAsciiDigit(ch)))) throw new ArgumentException("VAT number: 15 digits.");
        var cr = Optional(x.CrNo, "CR number", 20);
        if (cr is not null && cr.Any(ch => !char.IsAsciiDigit(ch))) throw new ArgumentException("CR number: digits only.");
        var phone = Optional(x.Phone, "Phone", 30);
        if (phone is not null && phone.Any(ch => !(char.IsAsciiDigit(ch) || ch is '+' or ' ' or '-'))) throw new ArgumentException("Phone: digits, spaces, + and - only.");
        var email = Optional(x.Email, "Email", 200);
        if (email is not null && !System.Net.Mail.MailAddress.TryCreate(email, out _)) throw new ArgumentException("Email: not a valid address.");
        if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.supplier WHERE name_en=@n AND supplier_id<>@i", token, ("@n", name), ("@i", id ?? 0)) is not null) throw new ArgumentException($"{name} is already a supplier.");
        (string, object?)[] v = [("@n", name), ("@v", vat), ("@cr", cr), ("@cn", Optional(x.ContactName, "Contact person", 200)), ("@p", phone), ("@e", email),
            ("@ad", Optional(x.Address, "Address", 300)), ("@nt", Optional(x.Notes, "Notes", 500)), ("@a", x.Active), ("@b", s.Id), ("@i", id ?? 0)];
        int saved;
        if (id is int existing)
        {
            if (await Scalar(c, tx, "SELECT 1 FROM itam_ref.supplier WHERE supplier_id=@i", token, ("@i", existing)) is null) throw new ArgumentException("The supplier was not found.");
            await Exec(c, tx, "UPDATE itam_ref.supplier SET name_en=@n,vat_no=@v,cr_no=@cr,contact_name=@cn,phone=@p,email=@e,address=@ad,notes=@nt,active=@a WHERE supplier_id=@i", token, v);
            saved = existing;
        }
        else saved = Convert.ToInt32(await Scalar(c, tx, "INSERT itam_ref.supplier(name_en,vat_no,cr_no,contact_name,phone,email,address,notes,active,created_by) OUTPUT inserted.supplier_id VALUES(@n,@v,@cr,@cn,@p,@e,@ad,@nt,@a,@b)", token, v));
        await Audit(c, tx, s.Id, "supplier", name, id is null ? "Add" : "Change", null, new { name, vat, cr, phone, email, x.Active }, null, token);
        await tx.CommitAsync(token);
        return new { id = saved };
    }
}
