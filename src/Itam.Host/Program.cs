using Itam.App;
using Itam.Host;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

// IT Assets production host (IIS). People sign in on the sign-in page with their AD username and password (owner, 22 October
// 2026); IIS allows anonymous access to the site and IT Assets checks every API call. Run once with --install-database (as an
// account allowed to change the database) to apply the database scripts; the site refuses to start while scripts are pending.
var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("ItamHost").Get<ItamHostSettings>() ?? throw new InvalidOperationException("The ItamHost settings are missing.");
settings.Validate();
var cs = settings.ConnectionString();

if (args.Contains("--install-database"))
{
    await using var c = new SqlConnection(cs);
    Console.WriteLine($"IT Assets: database scripts applied to {settings.Database}: {await ItamSchemaInstaller.Apply(c, settings.Database)} (of {ItamSchemaInstaller.Count}).");
    return;
}
await using (var c = new SqlConnection(cs))
    if (await ItamSchemaInstaller.Pending(c) is var pending && pending > 0)
        throw new InvalidOperationException($"{pending} database script(s) are not installed on {settings.Database}: run Itam.Host.exe --install-database first.");

byte[] Font(string file) => File.ReadAllBytes(Path.Combine(settings.FontsPath, file));
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy = CookieSecurePolicy.Always; });
builder.Services.AddItam(_ => new SqlConnection(cs), Font("arial.ttf"), settings.SiteUrl.TrimEnd('/') + "/a/", Font("calibri.ttf"), Font("calibrib.ttf"));
// OS and software keys are encrypted with the site's data-protection keys, kept in their own folder and protected by
// Windows for this server (DPAPI). Back the folder up with the database.
builder.Services.AddDataProtection().SetApplicationName("IT Assets").PersistKeysToFileSystem(new DirectoryInfo(settings.KeysPath)).ProtectKeysWithDpapi(protectToLocalMachine: true);
builder.Services.AddSingleton<IItamSecrets>(sp => new HostSecrets(sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("Itam.Keys.v1")));
builder.Services.AddScoped<IItamSession, ItamWindowsSession>();
builder.Services.AddItamSignIn();
if (settings.Directory.Enabled)
{
    builder.Services.AddSingleton(settings.Directory);
    builder.Services.AddSingleton<ItamLdapDirectory>();
    builder.Services.AddSingleton<IItamPasswordCheck, ItamLdapPasswordCheck>();
    builder.Services.AddSingleton<IItamComputerDirectory>(sp => sp.GetRequiredService<ItamLdapDirectory>());
    // The AD employee job: every SyncEveryMinutes (15), and "Read AD now" in the Administration Center.
    builder.Services.AddItamDirectorySync(sp => { var d = sp.GetRequiredService<ItamLdapDirectory>(); return t => Task.Run(d.Employees, t); }, settings.Directory.SyncEveryMinutes);
    builder.Services.AddHostedService<ItamAdCompareJob>();
}
if (settings.Mail.Enabled)
{
    var m = settings.Mail;
    builder.Services.AddSingleton<IItamMailer>(new ItamSmtpMailer(new ItamSmtpSettings(m.Host, m.Port, m.EnableSsl, m.From, m.FromName, m.UseDefaultCredentials)));
    builder.Services.AddSingleton(new ItamMailOptions(string.IsNullOrWhiteSpace(m.TestRecipient) ? null : m.TestRecipient.Trim()));
}

var app = builder.Build();
app.UseHsts();
app.UseAuthentication();
// A health page for IT monitoring: the site and its database answer (no data shown).
app.MapGet("/health", async () =>
{
    try { await using var c = new SqlConnection(cs); await c.OpenAsync(); return Results.Text("OK"); }
    catch (SqlException) { return Results.Text("Database unavailable", statusCode: 503); }
});
app.MapItamSignIn();
app.MapItam();
app.Logger.LogInformation("IT Assets started: {Database}, AD {Directory}, email {Mail}{Test}.", settings.Database, settings.Directory.Enabled ? "on" : "off",
    settings.Mail.Enabled ? "on" : "off (emails stay in the outbox)", settings.Mail.Enabled && !string.IsNullOrWhiteSpace(settings.Mail.TestRecipient) ? $" (test mailbox {settings.Mail.TestRecipient})" : "");
await app.RunAsync();

sealed class HostSecrets(IDataProtector protector) : IItamSecrets
{
    public string Protect(string value) => protector.Protect(value);
    public string Unprotect(string value) => protector.Unprotect(value);
}
