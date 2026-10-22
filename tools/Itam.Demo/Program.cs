using System.Net;
using Itam.App;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

// IT Assets local demo: loopback only, LocalDB only, invented people, assets and suppliers. No AD, SMTP or production database.
const string Database = "ITAM_Validation_20261022000000000_1a1a1a1a";
const string BaseUrl = "http://127.0.0.1:5340";
var master = new SqlConnectionStringBuilder { DataSource = @"(localdb)\MSSQLLocalDB", InitialCatalog = "master", IntegratedSecurity = true, TrustServerCertificate = true, Encrypt = true }.ConnectionString;
var cs = new SqlConnectionStringBuilder(master) { InitialCatalog = Database }.ConnectionString;
var reset = args.Contains("--reset");
await Exec(master, reset ? $"IF DB_ID(N'{Database}') IS NOT NULL BEGIN ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Database}]; END" : "SELECT 1");
await Exec(master, $"IF DB_ID(N'{Database}') IS NULL BEGIN CREATE DATABASE [{Database}]; ALTER DATABASE [{Database}] SET COMPATIBILITY_LEVEL=120; END");
await using (var c = new SqlConnection(cs)) Console.WriteLine($"IT Assets migrations applied now: {await ItamSchemaInstaller.Apply(c, Database)}");
var font = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"));
var calibri = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "calibri.ttf"));
var calibriBold = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "calibrib.ttf"));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(BaseUrl);
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; });
builder.Services.AddItam(_ => new SqlConnection(cs), font, BaseUrl + "/a/", calibri, calibriBold);
builder.Services.AddSingleton<IItamSecrets>(sp => new DemoSecrets(sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("Itam.Keys.v1")));
builder.Services.AddScoped<IItamSession, DemoSession>();
// The sign-in page as in production; in the demo any demo account signs in with the password "demo" (no AD).
builder.Services.AddItamSignIn(httpsOnly: false);
builder.Services.AddSingleton<IItamPasswordCheck, DemoPasswordCheck>();
builder.Services.AddSingleton<IItamComputerDirectory, DemoComputers>();
// The AD job with the invented demo directory (every 15 minutes, and "Read AD now").
builder.Services.AddItamDirectorySync(_ => _ => Task.FromResult(DemoSeed.People()), 15);
var app = builder.Build();

if (await Scalar(cs, "SELECT COUNT(*) FROM itam_access.employee") is 0)
{
    using var scope = app.Services.CreateScope();
    await DemoSeed.Run(cs, scope.ServiceProvider.GetRequiredService<ItamStore>(), new ItamPdf(font, calibri, calibriBold));
}

// Demo badge on every page: who is signed in, and a link back to the chooser (in the approved design).
app.MapGet("/demo-badge.css", () => Results.Text(
    "a.demo-badge,.x a.demo-badge{position:fixed;right:16px;bottom:16px;z-index:9999;display:inline-flex;align-items:center;gap:8px;padding:9px 16px 9px 9px;border-radius:999px;background:#1d4ed8;color:#fff;font:600 13px 'Segoe UI',Arial;text-decoration:none;box-shadow:0 8px 22px #1d4ed855}" +
    "a.demo-badge:hover{background:#2563eb;color:#fff}a.demo-badge b{background:#fff;color:#1d4ed8;border-radius:999px;padding:2px 9px;font-size:11px;letter-spacing:.06em}" +
    ".demo-main{max-width:1200px;margin:0 auto;padding:40px 32px 80px}.demo-cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:14px;margin-top:8px}" +
    ".x a.demo-card{display:flex;flex-direction:column;gap:6px;padding:18px 20px;border:1px solid var(--line);border-radius:16px;background:#fff;box-shadow:var(--sh);text-decoration:none;color:var(--ink);transition:border-color .15s,box-shadow .15s,transform .1s}" +
    ".x a.demo-card:hover{border-color:#93b4f5;box-shadow:0 0 0 4px #2563eb1f,var(--sh2);transform:translateY(-1px)}" +
    ".x a.demo-card.on{border:2px solid var(--acc);background:var(--accbg)}" +
    ".demo-card span{font-size:11.5px;letter-spacing:.08em;text-transform:uppercase;color:var(--acc2);font-weight:750}.demo-card strong{font-size:16px}" +
    ".demo-card em{font-style:normal;color:var(--sub);font-size:13px;line-height:1.45}.demo-card i{margin-top:6px;font-style:normal;font-weight:700;color:var(--acc);font-size:13px}", "text/css"));
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (!(path == "/" || path.EndsWith(".html", StringComparison.Ordinal))) { await next(); return; }
    var original = ctx.Response.Body; using var buffer = new MemoryStream(); ctx.Response.Body = buffer;
    try { await next(); } finally { ctx.Response.Body = original; }
    buffer.Position = 0; var html = await new StreamReader(buffer).ReadToEndAsync();
    var who = DemoPeople.Find(ctx.Request.Cookies["itam-demo-user"]);
    var badge = $"<link rel=\"stylesheet\" href=\"/demo-badge.css\"><a class=\"demo-badge\" href=\"/demo\"><b>DEMO</b>{WebUtility.HtmlEncode(who?.Role ?? "Choose who you are")} · Switch</a>";
    ctx.Response.ContentLength = null; await ctx.Response.WriteAsync(html.Replace("</body>", badge + "</body>", StringComparison.Ordinal));
});
app.MapGet("/demo", (HttpContext ctx) => Results.Content(DemoPage(ctx.Request.Cookies["itam-demo-user"]), "text/html; charset=utf-8"));
app.MapGet("/demo/as/{n:int}", (HttpContext ctx, int n, string? to) =>
{
    var p = DemoPeople.All.ElementAtOrDefault(n); if (p is null) return Results.NotFound();
    ctx.Response.Cookies.Append("itam-demo-user", p.Id.ToString(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax });
    // Optional page to open after choosing the person (local paths only), used for review screenshots.
    return Results.Redirect(to is { Length: > 1 } && to[0] == '/' && to[1] != '/' && to[1] != '\\' ? to : p.Start);
});
app.UseAuthentication();
app.MapItamSignIn();
app.MapItam();
Console.WriteLine($"IT Assets demo: {BaseUrl}/demo (invented data, LocalDB; never deployed)");
await app.RunAsync();

static async Task Exec(string cs, string sql) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand(sql, c) { CommandTimeout = 120 }; await q.ExecuteNonQueryAsync(); }
static async Task<object?> Scalar(string cs, string sql) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand(sql, c); return await q.ExecuteScalarAsync(); }

static string DemoPage(string? current)
{
    var me = DemoPeople.Find(current);
    var cards = string.Concat(DemoPeople.All.Select((p, i) => p.Card is null ? "" :
        $"<a class=\"demo-card{(p == me ? " on" : "")}\" href=\"/demo/as/{i}\"><span>{WebUtility.HtmlEncode(p.Role)}</span><strong>{WebUtility.HtmlEncode(p.Name)}</strong><em>{WebUtility.HtmlEncode(p.Card)}</em><i>{(p == me ? "Signed in now" : "Sign in as this person ›")}</i></a>"));
    return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=1280\"><title>IT Assets · Demo</title><link rel=\"stylesheet\" href=\"/itam.css\"><link rel=\"stylesheet\" href=\"/demo-badge.css\"></head><body class=\"x\"><main class=\"demo-main\">" +
        "<div class=\"page-head\"><div><p class=\"eyebrow\">Local demo · invented data</p><h1>IT Assets demo: choose who you are</h1><p>Steps 1 and 2 of the build plan: asset register, purchase orders with the PO pack, handover with paper-signed delivery notes, labels, roles, master data and My Backup. Click a card to sign in as that invented person; use the blue Switch button at the bottom right to come back here.</p></div></div>" +
        $"<div class=\"demo-cards\">{cards}</div></main></body></html>";
}

sealed record DemoPerson(Guid Id, string Account, string Name, string Role, string? Card, string Start = "/");
static class DemoPeople
{
    static Guid G(int n) => new($"00000000-0000-0000-0000-{n:000000000000}");
    public static readonly DemoPerson[] All =
    [
        new(G(1), "demo.assets", "Demo Assets Officer", "IT Assets Officer + IT Support Specialist (Central)", "Registers assets, records POs and receives devices, prints the PO pack, sees keys"),
        new(G(2), "demo.manager", "Demo IT Manager", "IT Manager + System Administrator", "Sees everything, gives roles, reads the audit log; approves maintenance; AD and CUCM tasks"),
        new(G(3), "demo.leader", "Demo Team Leader", "IT Helpdesk Team Leader", "All regions; records POs and receives devices"),
        new(G(4), "demo.west", "Demo Specialist West", "IT Support Specialist (West)", "Assets of the West region only"),
        new(G(5), "demo.central", "Demo Specialist Central and East", "IT Support Specialist (Central, East)", "Two regions in one list"),
        new(G(6), "demo.sysadmin", "Demo System Administrator", "System Administrator", "Computers only (read); sees POs"),
        new(G(7), "demo.voice", "Demo Voice Administrator", "Voice Administrator", "IP phones only: MAC, IP address and extension", "/?type=IP"),
        new(G(8), "demo.south", "Demo Specialist South", "IT Support Specialist (South)", "Assets, handover and keys of the South region; POs; maintenance orders"),
        new(G(9), "demo.employee4", "Demo Employee Four", "Employee", null),
        new(G(10), "demo.employee", "Demo Employee", "Employee (no IT role)", "No IT role: sees no assets yet (My IT Assets comes in step 3)"),
        new(G(11), "demo.employee2", "Demo Employee Two", "Employee", null), new(G(12), "demo.employee3", "Demo Employee Three", "Employee", null),
        new(G(13), "demo.ink", "Demo Ink Officer", "Ink Officer + IT Support Specialist (South)", "Sends ink requests for approval, orders and delivers ink, records toner invoices", "/ink.html")
    ];
    public static DemoPerson? Find(string? id) => Guid.TryParse(id, out var g) ? All.FirstOrDefault(p => p.Id == g) : null;
}

// The person chosen on /demo, or else the person signed in on the sign-in page.
sealed class DemoSession(ItamStore store) : IItamSession
{
    public async Task<ItamUser?> Current(HttpContext context, CancellationToken token)
        => DemoPeople.Find(context.Request.Cookies["itam-demo-user"]) is { } p ? new ItamUser(p.Id, p.Account) : await new ItamWindowsSession(store).Current(context, token);
}

sealed class DemoPasswordCheck : IItamPasswordCheck
{
    public Task<bool> Check(string account, string password, CancellationToken token) => Task.FromResult(password == "demo");
}

sealed class DemoSecrets(IDataProtector protector) : IItamSecrets
{
    public string Protect(string value) => protector.Protect(value);
    public string Unprotect(string value) => protector.Unprotect(value);
}

/// <summary>Invented computer accounts for the AD comparison demo (no real Active Directory is read).</summary>
sealed class DemoComputers : IItamComputerDirectory
{
    public string ComparedOus => "CN=Computers; OU=ALTAWKILAT Computers (with sub-OUs); OU=Test (demo)";
    public Task<IReadOnlyList<ItamAdComputer>> Computers(CancellationToken token)
    {
        var now = DateTime.UtcNow; const string ou = "OU=Computers,OU=ALTAWKILAT Computers";
        IReadOnlyList<ItamAdComputer> list =
        [
            new("ATWK-DEMO-001", ou, true, now.AddDays(-1), now.AddYears(-2), "Windows 11 Enterprise", true),
            new("ATWK-DEMO-033", ou, false, now.AddDays(-140), now.AddYears(-3), "Windows 10 Enterprise", true),
            new("ATWK-DEMO-009", ou, true, now.AddDays(-3), now.AddYears(-4), "Windows 10 Enterprise", true),
            new("ATWK-DEMO-050", ou, true, now.AddDays(-2), now.AddMonths(-8), "Windows 11 Enterprise", true),
            new("ATWK-TRAIN-01", "OU=Test", true, now.AddDays(-10), now.AddYears(-1), "Windows 11 Enterprise", true),
            new("ATWK-DEMO-002", "OU=Old Computers", true, now.AddDays(-4), now.AddYears(-3), "Windows 10 Enterprise", false)
        ];
        return Task.FromResult(list);
    }
}
