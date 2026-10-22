using System.Globalization;
using System.Net.Mail;
using System.Net.Mime;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Itam.App;

/// <summary>Windows sign-in (production): IIS authenticates the person (Windows authentication, no anonymous access); IT Assets
/// finds the enabled employee with the same account in its copy of Active Directory. Nobody signs in with a password in
/// IT Assets. A disabled account, or an account not yet copied from AD, is not let in.</summary>
public sealed class ItamWindowsSession(ItamStore store) : IItamSession
{
    public async Task<ItamUser?> Current(HttpContext context, CancellationToken token)
    {
        if (context.User.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } name }) return null;
        var account = Account(name);
        if (account is null) return null;
        return await store.EmployeeByAccount(account, token) is Guid id ? new ItamUser(id, account) : null;
    }

    /// <summary>DOMAIN\user or user@domain to the sAMAccountName (lower case).</summary>
    public static string? Account(string identity)
    {
        var i = identity.IndexOf('\\');
        var account = i >= 0 ? identity[(i + 1)..] : identity.Split('@')[0];
        account = account.Trim().ToLowerInvariant();
        return account.Length is > 0 and <= 64 && !account.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)) ? account : null;
    }
}

/// <summary>The company mail relay (as CMS): host, port, TLS, the sender address and name. The relay accepts the server
/// (anonymous or the site's own Windows identity); IT Assets stores no mail password.</summary>
public sealed record ItamSmtpSettings(string Host, int Port, bool EnableSsl, string From, string FromName = "IT Assets", bool UseDefaultCredentials = false, string? PickupDirectory = null);

/// <summary>Sends the outbox emails through the relay: HTML with the logo embedded (cid:logo), a plain-text copy, To, CC and
/// Reply-To (the person who acted). A pickup folder (tests) writes the email as a file instead of sending it.</summary>
public sealed class ItamSmtpMailer(ItamSmtpSettings settings) : IItamMailer
{
    public static string PlainText(string html)
    {
        var t = Regex.Replace(html, @"<(br|/p|/div|/tr|/table)[^>]*>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "<[^>]+>", " ");
        t = System.Net.WebUtility.HtmlDecode(t);
        return Regex.Replace(Regex.Replace(t, @"[ \t]+", " "), @"\s*\n\s*", "\n").Trim();
    }

    public MailMessage Build(ItamMailMessage m)
    {
        if (m.To.Count is 0 or > 250 || m.Cc.Count > 250 || string.IsNullOrWhiteSpace(m.Subject) || m.Subject.Length > 998 || m.Subject.Any(char.IsControl))
            throw new ArgumentException("Invalid email.");
        var mail = new MailMessage { From = new MailAddress(settings.From, settings.FromName), Subject = m.Subject, SubjectEncoding = System.Text.Encoding.UTF8 };
        foreach (var to in m.To) mail.To.Add(to);
        foreach (var cc in m.Cc) mail.CC.Add(cc);
        if (m.ReplyTo is { Length: > 0 } reply && MailAddress.TryCreate(reply, out var replyTo)) mail.ReplyToList.Add(replyTo);
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(PlainText(m.Html), System.Text.Encoding.UTF8, MediaTypeNames.Text.Plain));
        var view = AlternateView.CreateAlternateViewFromString(m.Html, System.Text.Encoding.UTF8, MediaTypeNames.Text.Html);
        foreach (var image in m.Images.Where(i => i.Content.Length > 0))
        {
            var resource = new LinkedResource(new MemoryStream(image.Content, false), image.ContentType) { ContentId = image.ContentId };
            resource.ContentType.Name = image.FileName;
            view.LinkedResources.Add(resource);
        }
        mail.AlternateViews.Add(view);
        return mail;
    }

    public async Task Send(ItamMailMessage message, CancellationToken token)
    {
        using var mail = Build(message);
        using var client = settings.PickupDirectory is { Length: > 0 } pickup
            ? new SmtpClient { DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory, PickupDirectoryLocation = pickup }
            : new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.EnableSsl, UseDefaultCredentials = settings.UseDefaultCredentials };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await client.SendMailAsync(mail, timeout.Token);
    }
}

/// <summary>Active Directory to IT Assets: the attributes VTMS reads (build plan, "Active Directory attributes"). The national ID
/// (extensionAttribute6) is never read.</summary>
public static class ItamDirectoryMapping
{
    /// <summary>The user attributes read from AD (sAMAccountName, name, email, job description, department, company, office,
    /// region, HR code, SAP ID, mobile, IP phone, manager, enabled).</summary>
    public static readonly string[] UserAttributes = ["objectGUID", "distinguishedName", "sAMAccountName", "displayName", "mail", "userPrincipalName", "description", "department",
        "company", "physicalDeliveryOfficeName", "postalCode", "postOfficeBox", "streetAddress", "mobile", "ipPhone", "manager", "userAccountControl"];
    public static readonly string[] ComputerAttributes = ["name", "distinguishedName", "userAccountControl", "lastLogonTimestamp", "whenCreated", "operatingSystem"];

    static string? Clean(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().Length > max ? v.Trim()[..max] : v.Trim();
    static bool Disabled(string? uac) => int.TryParse(uac, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) && (f & 2) != 0;

    /// <summary>One AD user to the IT Assets copy; null when it cannot be an employee (no account or name).</summary>
    public static ItamEmployee? Employee(Guid id, Func<string, string?> attr, Func<string, Guid?> managerByDn)
    {
        var account = Clean(attr("sAMAccountName"), 64)?.ToLowerInvariant(); var name = Clean(attr("displayName"), 200);
        if (account is null || name is null || id == Guid.Empty) return null;
        var mail = Clean(attr("mail"), 254) ?? Clean(attr("userPrincipalName"), 254);
        if (mail is not null && !(MailAddress.TryCreate(mail, out var a) && a.Address == mail)) mail = null;
        var manager = attr("manager") is { Length: > 0 } dn ? managerByDn(dn) : null;
        return new ItamEmployee(id, account, name, mail, Clean(attr("description"), 200), Clean(attr("department"), 200), manager == id ? null : manager, !Disabled(attr("userAccountControl")),
            Clean(attr("mobile"), 50), Clean(attr("physicalDeliveryOfficeName"), 200), Clean(attr("company"), 200), Clean(attr("postOfficeBox"), 50), Clean(attr("postalCode"), 100),
            Clean(attr("streetAddress"), 50), Clean(attr("ipPhone"), 50));
    }

    /// <summary>One AD computer for the comparison. comparedOus: the OUs compared (DN, with or without their sub-OUs).</summary>
    public static ItamAdComputer? Computer(Func<string, string?> attr, IReadOnlyList<(string Dn, bool SubOus)> comparedOus, IReadOnlyList<Regex> leaveOut)
    {
        var name = Clean(attr("name"), 64); var dn = Clean(attr("distinguishedName"), 400);
        if (name is null || dn is null || leaveOut.Any(r => r.IsMatch(name))) return null;
        var parent = dn.Contains(',') ? dn[(dn.IndexOf(',') + 1)..] : "";
        var inScope = comparedOus.Any(o => string.Equals(parent, o.Dn, StringComparison.OrdinalIgnoreCase) || (o.SubOus && parent.EndsWith("," + o.Dn, StringComparison.OrdinalIgnoreCase)));
        DateTime? lastLogon = long.TryParse(attr("lastLogonTimestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ft) && ft > 0 ? DateTime.FromFileTimeUtc(ft) : null;
        DateTime? created = DateTime.TryParseExact(attr("whenCreated"), "yyyyMMddHHmmss.0'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var w) ? w : null;
        return new ItamAdComputer(name, parent, !Disabled(attr("userAccountControl")), lastLogon, created, Clean(attr("operatingSystem"), 100), inScope);
    }
}
