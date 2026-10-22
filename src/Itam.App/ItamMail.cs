using System.Globalization;
using System.Net;
using System.Text;

namespace Itam.App;

/// <summary>An image embedded in the email and referenced as cid:ContentId (the logo).</summary>
public sealed record ItamMailImage(string ContentId, string FileName, string ContentType, byte[] Content);
/// <summary>One email as it leaves the outbox.</summary>
public sealed record ItamMailMessage(IReadOnlyList<string> To, IReadOnlyList<string> Cc, string Subject, string Html, IReadOnlyList<ItamMailImage> Images);

/// <summary>The mail service (SMTP in production, configured at the production step). When none is registered, emails stay
/// queued in the outbox and nothing leaves the server.</summary>
public interface IItamMailer { Task Send(ItamMailMessage message, CancellationToken token); }

/// <summary>What an email shows: the action sentence, the details table, extra sections and the link to the record.</summary>
public sealed record ItamMailView(string Template, string Name, string Reference, string Greeting, string ActionLine,
    IReadOnlyList<(string Label, string? Value)> Details, IReadOnlyList<(string Title, string Text)> Sections, string Link, string LinkText, string? ActionBy,
    string? ApproveLink = null, string? RejectLink = null);

/// <summary>The IT Assets email layout, the same as the CMS emails (owner, 22 October 2026): a centred white card on a light
/// background, the logo, the system name underlined in navy, the automatic-notification line, "Dear ..." in red, the
/// action sentence, a two-column details table with shaded labels, sections with red headings, the link to the record and
/// the signature. All values are HTML-encoded; nothing a user typed is treated as HTML.</summary>
public static class ItamMailLayout
{
    const string Font = "font-family:Calibri,Arial,sans-serif;font-size:14px;line-height:1.4;color:#1f2937";
    const int CardWidth = 720, LabelWidth = 130, ValueWidth = 206;
    const string Line = "#c7d2e0", Navy = "#0b2a5b", Red = "#c00000", Shade = "#eef2f8";
    static string E(string? value) => WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(value) ? "—" : value.Trim());

    public static string Html(ItamMailView v, string? testNotice)
    {
        const string label = "background:" + Shade + ";color:" + Navy + ";font-weight:bold;font-size:13px;padding:6px 10px;border:1px solid " + Line + ";vertical-align:top";
        const string cell = "font-size:13px;padding:6px 10px;border:1px solid " + Line + ";vertical-align:top";
        const string heading = "background:" + Shade + ";color:" + Red + ";font-weight:bold;font-size:13px;padding:6px 10px;border:1px solid " + Line;
        const string table = "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:100%;border-collapse:collapse;margin:0 0 12px;" + Font + "\">";
        string Td(string style, int width, string content) => "<td width=\"" + width + "\" style=\"" + style + ";width:" + width + "px\">" + content + "</td>";
        var link = "<a href=\"" + WebUtility.HtmlEncode(v.Link) + "\" style=\"color:" + Navy + ";font-weight:bold;text-decoration:underline\">" + E(v.LinkText) + " &#8250;</a>";
        var details = new StringBuilder(table);
        var pairs = v.Details.Where(d => d.Value is not null).Select(d => (Label: d.Label, Value: E(d.Value))).Append((Label: "Record", Value: link)).ToList();
        for (var i = 0; i < pairs.Count; i += 2)
        {
            details.Append("<tr>").Append(Td(label, LabelWidth, E(pairs[i].Label)));
            if (i + 1 < pairs.Count) details.Append(Td(cell, ValueWidth, pairs[i].Value)).Append(Td(label, LabelWidth, E(pairs[i + 1].Label))).Append(Td(cell, ValueWidth, pairs[i + 1].Value));
            else details.Append("<td colspan=\"3\" style=\"").Append(cell).Append("\">").Append(pairs[i].Value).Append("</td>");
            details.Append("</tr>");
        }
        details.Append("</table>");
        var sections = string.Concat(v.Sections.Where(s => !string.IsNullOrWhiteSpace(s.Text)).Select(s => table + "<tr><td style=\"" + heading + "\">" + E(s.Title) + "</td></tr><tr><td style=\"" + cell +
            ";font-size:14px;padding:8px 10px\" dir=\"auto\">" + E(s.Text).Replace("\n", "<br>") + "</td></tr></table>"));
        var body = "<div style=\"" + Font + "\">" +
            "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\"><tr><td align=\"center\" style=\"padding:0 0 4px\"><img src=\"cid:logo\" alt=\"ALTAWKILAT\" width=\"200\" height=\"56\" style=\"display:block;margin:0 auto;border:0;width:200px;height:56px\"></td></tr></table>" +
            "<div style=\"text-align:center;color:" + Navy + ";font-size:19px;font-weight:bold;margin:6px 0 0;padding-bottom:10px;border-bottom:3px solid " + Navy + "\">IT Assets Management System</div>" +
            "<div style=\"color:#6b7280;font-size:12px;margin:8px 0 14px\">This is an automatic notification, please don't reply to it.</div>" +
            "<div style=\"color:" + Red + ";font-weight:bold;font-size:15px;margin-bottom:6px\">Dear " + E(v.Greeting) + ",</div>" +
            "<div style=\"margin-bottom:14px\">" + E(v.ActionLine) + (v.ApproveLink is null ? " Please action as appropriate by clicking the link below." : " One click on Approve records your approval (you may be asked to sign in); Reject asks for a short reason.") + "</div>" +
            Buttons(v) +
            details + sections +
            "<div style=\"margin-top:16px;color:" + Red + ";font-weight:bold\">Best Regards</div>" +
            (string.IsNullOrWhiteSpace(v.ActionBy) ? "" : "<div style=\"font-weight:bold;color:" + Navy + "\">" + E(v.ActionBy) + "</div>") +
            "<div style=\"font-weight:bold;color:" + Navy + "\">ALTAWKILAT IT Team</div></div>";
        var h = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + E(v.Name) + "</title></head>" +
            "<body style=\"margin:0;padding:0;background:#eef1f5\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" bgcolor=\"#eef1f5\" style=\"background:#eef1f5\"><tr><td align=\"center\" style=\"padding:20px 8px\">");
        if (testNotice is not null)
            h.Append("<table role=\"presentation\" width=\"").Append(CardWidth).Append("\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:").Append(CardWidth)
             .Append("px;max-width:100%;margin-bottom:8px\"><tr><td bgcolor=\"#fff4e5\" style=\"background:#fff4e5;border:1px solid #f0b429;color:#7a4b00;padding:6px 10px;font-size:12px;font-family:Calibri,Arial,sans-serif\">")
             .Append(E(testNotice)).Append("</td></tr></table>");
        h.Append("<table role=\"presentation\" width=\"").Append(CardWidth).Append("\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" bgcolor=\"#ffffff\" style=\"width:").Append(CardWidth)
         .Append("px;max-width:100%;background:#ffffff;border:1px solid #d5dce6\"><tr><td style=\"padding:22px 24px 20px;").Append(Font).Append("\">").Append(body).Append("</td></tr></table>")
         .Append("<div style=\"font-family:Calibri,Arial,sans-serif;font-size:11px;color:#8a94a6;padding-top:10px\">IT Assets Management System · Altawkilat · ").Append(E(v.Template)).Append("</div>");
        return h.Append("</td></tr></table></body></html>").ToString();
    }

    // Approve (green) and Reject (red) as in the owner's IT-EM-31 template; table-based so Outlook shows them as buttons.
    static string Buttons(ItamMailView v)
    {
        if (v.ApproveLink is null || v.RejectLink is null) return "";
        string B(string href, string text, string colour) => "<td style=\"padding:0 12px 0 0\"><table role=\"presentation\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\"><tr><td bgcolor=\"" + colour + "\" style=\"background:" + colour + ";padding:9px 26px;border-radius:4px\">" +
            "<a href=\"" + WebUtility.HtmlEncode(href) + "\" style=\"font-family:Calibri,Arial,sans-serif;font-size:15px;font-weight:bold;color:#ffffff;text-decoration:none;display:inline-block\">" + text + "</a></td></tr></table></td>";
        return "<table role=\"presentation\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"margin:0 0 16px\"><tr>" + B(v.ApproveLink, "Approve", "#2E7D32") + B(v.RejectLink, "Reject", "#C00000") + "</tr></table>";
    }

    public static string Date(DateOnly? d) => d?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "—";
    public static string When(DateTime? utc) => utc?.AddHours(3).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? "—";
    public static string Sar(decimal? m) => m is null ? "—" : m.Value.ToString("#,0.00", CultureInfo.InvariantCulture) + " SAR";
}
