using Microsoft.Data.SqlClient;

namespace Itam.Host;

/// <summary>The site's settings (appsettings.Production.json, section "ItamHost"). They hold no password: SQL Server, Active
/// Directory and the mail relay accept the site's own Windows identity (the IIS application pool identity).</summary>
public sealed class ItamHostSettings
{
    /// <summary>ITAM_Production or ITAM_Test.</summary>
    public string Database { get; set; } = "";
    /// <summary>The SQL Server instance, for example SQL01 or SQL01\INSTANCE.</summary>
    public string SqlServer { get; set; } = "";
    /// <summary>Trust the SQL Server certificate (as the shared platform on the same server does); the connection stays encrypted.</summary>
    public bool SqlTrustServerCertificate { get; set; }
    /// <summary>The site's address as users open it, for the links in emails and on labels, for example https://itassets.altawkilat.com.</summary>
    public string SiteUrl { get; set; } = "";
    /// <summary>The folder holding the data-protection keys that encrypt OS and software keys. Back it up with the database:
    /// without it the stored keys cannot be read.</summary>
    public string KeysPath { get; set; } = "";
    public string FontsPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    public DirectorySettings Directory { get; set; } = new();
    public MailSettings Mail { get; set; } = new();

    public string ConnectionString() => new SqlConnectionStringBuilder
    { DataSource = SqlServer, InitialCatalog = Database, IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = SqlTrustServerCertificate, ApplicationName = "IT Assets" }.ConnectionString;

    /// <summary>Refuses settings that would point the site at the wrong place.</summary>
    public void Validate()
    {
        Itam.App.ItamSchemaInstaller.RequireTarget(Database);
        if (string.IsNullOrWhiteSpace(SqlServer) || SqlServer.Contains(';') || SqlServer.Contains('='))
            throw new InvalidOperationException("ItamHost:SqlServer must be the SQL Server name only (Windows authentication; no password in the settings).");
        if (!Uri.TryCreate(SiteUrl, UriKind.Absolute, out var site) || site.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("ItamHost:SiteUrl must be the https address of the site.");
        if (string.IsNullOrWhiteSpace(KeysPath) || !Path.IsPathRooted(KeysPath))
            throw new InvalidOperationException("ItamHost:KeysPath must be a full folder path outside the site folder.");
        if (!Directory.Enabled) throw new InvalidOperationException("ItamHost:Directory:Enabled must be true: people sign in with their AD username and password.");
        Directory.Validate();
        if (Mail.Enabled) Mail.Validate();
    }
}

public sealed class DirectorySettings
{
    public bool Enabled { get; set; }
    /// <summary>A domain controller (or the domain name) for LDAPS on port 636.</summary>
    public string Host { get; set; } = "";
    /// <summary>The users OU, with its sub-OUs (decided 19 October 2026): OU=Users,OU=ALTAWKILAT Users,DC=altawkilat,DC=com.</summary>
    public string UsersBaseDn { get; set; } = "";
    /// <summary>The whole domain, to find a computer outside the compared OUs: DC=altawkilat,DC=com.</summary>
    public string DomainBaseDn { get; set; } = "";
    /// <summary>The OUs compared with the delivery notes (decided 19 October 2026).</summary>
    public List<ComparedOu> ComparedOus { get; set; } = [];
    /// <summary>Computer name patterns left out of the comparison (regular expressions), for example servers.</summary>
    public List<string> LeaveOutNames { get; set; } = [];
    public int SyncEveryMinutes { get; set; } = 15;
    /// <summary>The local (Riyadh) hour of the daily AD comparison.</summary>
    public int CompareAtHour { get; set; } = 6;

    public void Validate()
    {
        if (Uri.CheckHostName(Host) is not (UriHostNameType.Dns or UriHostNameType.IPv4)) throw new InvalidOperationException("ItamHost:Directory:Host must be a domain controller name.");
        foreach (var dn in new[] { UsersBaseDn, DomainBaseDn }.Concat(ComparedOus.Select(o => o.Dn)))
            if (string.IsNullOrWhiteSpace(dn) || !dn.Contains("DC=", StringComparison.OrdinalIgnoreCase) || dn.Contains('\0')) throw new InvalidOperationException($"ItamHost:Directory: \"{dn}\" is not a distinguished name.");
        if (SyncEveryMinutes is < 15 or > 1440 || CompareAtHour is < 0 or > 23) throw new InvalidOperationException("ItamHost:Directory: SyncEveryMinutes 15-1440, CompareAtHour 0-23.");
    }
}

public sealed class ComparedOu { public string Dn { get; set; } = ""; public bool SubOus { get; set; } = true; }

public sealed class MailSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public bool EnableSsl { get; set; }
    public string From { get; set; } = "";
    public string FromName { get; set; } = "IT Assets";
    /// <summary>The relay accepts the site's Windows identity (no mail password is ever stored).</summary>
    public bool UseDefaultCredentials { get; set; }
    /// <summary>Test phase (as CMS): every email goes to this one mailbox, with the real recipients named in it.</summary>
    public string? TestRecipient { get; set; }

    public void Validate()
    {
        if (Uri.CheckHostName(Host) == UriHostNameType.Unknown || Port is < 1 or > 65535) throw new InvalidOperationException("ItamHost:Mail: Host and Port of the mail relay.");
        if (!System.Net.Mail.MailAddress.TryCreate(From, out var from) || from.Address != From) throw new InvalidOperationException("ItamHost:Mail:From must be an email address.");
        if (TestRecipient is { Length: > 0 } t && !(System.Net.Mail.MailAddress.TryCreate(t, out var test) && test.Address == t)) throw new InvalidOperationException("ItamHost:Mail:TestRecipient must be an email address.");
    }
}
