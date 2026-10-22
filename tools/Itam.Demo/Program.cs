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

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(BaseUrl);
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; });
builder.Services.AddItam(_ => new SqlConnection(cs), font, BaseUrl + "/a/");
builder.Services.AddSingleton<IItamSecrets>(sp => new DemoSecrets(sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("Itam.Keys.v1")));
builder.Services.AddScoped<IItamSession, DemoSession>();
var app = builder.Build();

if (await Scalar(cs, "SELECT COUNT(*) FROM itam_access.employee") is 0)
{
    using var scope = app.Services.CreateScope();
    await DemoSeed.Run(cs, scope.ServiceProvider.GetRequiredService<ItamStore>(), new ItamPdf(font));
}

// Demo badge on every page: who is signed in, and a link back to the chooser.
app.MapGet("/demo-badge.css", () => Results.Text(".demo-badge{position:fixed;right:16px;bottom:16px;z-index:9999;padding:9px 14px;border-radius:999px;background:#123453;color:#fff;font:600 12px 'Segoe UI',Arial;text-decoration:none;box-shadow:0 6px 18px #0004}.demo-badge b{background:#2e9e5b;border-radius:999px;padding:2px 8px;margin-right:6px}" +
    ".demo-cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:12px}.demo-cards .card{display:flex;flex-direction:column;gap:4px;text-decoration:none;color:var(--ink);margin:0}" +
    ".demo-cards .card:hover{border-color:#8fb6d9}.demo-cards .card span{font-size:11px;letter-spacing:.5px;text-transform:uppercase;color:var(--blue);font-weight:700}" +
    ".demo-cards .card strong{font-size:15px;color:var(--navy)}.demo-cards .card em{font-style:normal;color:var(--muted);font-size:12px}", "text/css"));
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
app.MapGet("/demo", () => Results.Content(DemoPage(), "text/html; charset=utf-8"));
app.MapGet("/demo/as/{n:int}", (HttpContext ctx, int n, string? to) =>
{
    var p = DemoPeople.All.ElementAtOrDefault(n); if (p is null) return Results.NotFound();
    ctx.Response.Cookies.Append("itam-demo-user", p.Id.ToString(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax });
    // Optional page to open after choosing the person (local paths only), used for review screenshots.
    return Results.Redirect(to is { Length: > 1 } && to[0] == '/' && to[1] != '/' && to[1] != '\\' ? to : p.Start);
});
app.MapItam();
Console.WriteLine($"IT Assets demo: {BaseUrl}/demo (invented data, LocalDB; never deployed)");
await app.RunAsync();

static async Task Exec(string cs, string sql) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand(sql, c) { CommandTimeout = 120 }; await q.ExecuteNonQueryAsync(); }
static async Task<object?> Scalar(string cs, string sql) { await using var c = new SqlConnection(cs); await c.OpenAsync(); using var q = new SqlCommand(sql, c); return await q.ExecuteScalarAsync(); }

static string DemoPage()
{
    var cards = string.Concat(DemoPeople.All.Select((p, i) => p.Card is null ? "" : $"<a class=\"card\" href=\"/demo/as/{i}\"><span>{WebUtility.HtmlEncode(p.Role)}</span><strong>{WebUtility.HtmlEncode(p.Name)}</strong><em>{WebUtility.HtmlEncode(p.Card)}</em></a>"));
    return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>IT Assets · Demo</title><link rel=\"stylesheet\" href=\"/itam.css\"><link rel=\"stylesheet\" href=\"/demo-badge.css\"></head><body><main>" +
        "<div class=\"page-head\"><div><p class=\"eyebrow\">Local demo · invented data</p><h1>IT Assets demo: choose who you are</h1><p>Step 1 of the build plan: asset register, purchase orders with receiving and the PO pack, roles, master data and My Backup. Each card signs you in as one invented person.</p></div></div>" +
        $"<div class=\"demo-cards\">{cards}</div></main></body></html>";
}

sealed record DemoPerson(Guid Id, string Account, string Name, string Role, string? Card, string Start = "/");
static class DemoPeople
{
    static Guid G(int n) => new($"00000000-0000-0000-0000-{n:000000000000}");
    public static readonly DemoPerson[] All =
    [
        new(G(1), "demo.assets", "Demo Assets Officer", "IT Assets Officer", "Registers assets, records POs and receives devices, prints the PO pack, sees keys"),
        new(G(2), "demo.manager", "Demo IT Manager", "IT Manager", "Sees everything, gives roles, reads the audit log"),
        new(G(3), "demo.leader", "Demo Team Leader", "IT Helpdesk Team Leader", "All regions; records POs and receives devices"),
        new(G(4), "demo.west", "Demo Specialist West", "IT Support Specialist (West)", "Assets of the West region only"),
        new(G(5), "demo.central", "Demo Specialist Central and East", "IT Support Specialist (Central, East)", "Two regions in one list"),
        new(G(6), "demo.sysadmin", "Demo System Administrator", "System Administrator", "Computers only (read); sees POs"),
        new(G(7), "demo.voice", "Demo Voice Administrator", "Voice Administrator", "IP phones only: MAC, IP address and extension", "/?type=IP"),
        new(G(8), "demo.itadmin", "Demo IT Administrator", "IT Administrator", "Master data, roles, settings; sees keys", "/admin.html"),
        new(G(9), "demo.auditor", "Demo Auditor", "Auditor", "Reads everything, changes nothing"),
        new(G(10), "demo.employee", "Demo Employee", "Employee (no IT role)", "No IT role: sees no assets yet (My IT Assets comes in step 3)"),
        new(G(11), "demo.employee2", "Demo Employee Two", "Employee", null), new(G(12), "demo.employee3", "Demo Employee Three", "Employee", null)
    ];
    public static DemoPerson? Find(string? id) => Guid.TryParse(id, out var g) ? All.FirstOrDefault(p => p.Id == g) : null;
}

sealed class DemoSession : IItamSession
{
    public Task<ItamUser?> Current(HttpContext context, CancellationToken token)
        => Task.FromResult(DemoPeople.Find(context.Request.Cookies["itam-demo-user"]) is { } p ? new ItamUser(p.Id, p.Account) : (ItamUser?)null);
}

sealed class DemoSecrets(IDataProtector protector) : IItamSecrets
{
    public string Protect(string value) => protector.Protect(value);
    public string Unprotect(string value) => protector.Unprotect(value);
}
