using Microsoft.AspNetCore.Http;

namespace Itam.App;

/// <summary>The signed-in Windows account (production: Windows sign-in; demo: chosen person), keyed by the stable
/// Active Directory objectGUID.</summary>
public sealed record ItamUser(Guid Id, string Account);

/// <summary>One employee as read from the Active Directory users OU (read-only).</summary>
public sealed record ItamEmployee(Guid Id, string Account, string Name, string? Email, string? JobTitle, string? Department, Guid? ManagerId, bool Enabled);

public interface IItamSession { Task<ItamUser?> Current(HttpContext context, CancellationToken token); }

/// <summary>Encrypts OS and software keys (production: the site's data-protection keys).</summary>
public interface IItamSecrets { string Protect(string value); string Unprotect(string value); }

public sealed class ItamAccessDenied(string message) : Exception(message);
public sealed class ItamConflict(string message) : Exception(message);

public static class ItamRoles
{
    public const string SupportSpecialist = "SupportSpecialist", TeamLeader = "TeamLeader", SystemAdministrator = "SystemAdministrator",
        VoiceAdministrator = "VoiceAdministrator", AssetsOfficer = "AssetsOfficer", ItManager = "ItManager", ItAdministrator = "ItAdministrator", Auditor = "Auditor";
    public static readonly string[] All = [SupportSpecialist, TeamLeader, SystemAdministrator, VoiceAdministrator, AssetsOfficer, ItManager, ItAdministrator, Auditor];
    public static string Label(string code) => code switch
    {
        SupportSpecialist => "IT Support Specialist", TeamLeader => "IT Helpdesk Team Leader", SystemAdministrator => "System Administrator",
        VoiceAdministrator => "Voice Administrator", AssetsOfficer => "IT Assets Officer", ItManager => "IT Manager",
        ItAdministrator => "IT Administrator", Auditor => "Auditor", _ => code
    };
}

public static class ItamStatuses
{
    public static readonly (string Code, string Label)[] All = [("InStore", "In Store"), ("InUse", "In Use"), ("InTransit", "In Transit"), ("InRepair", "In Repair"),
        ("PendingDisposal", "Pending disposal approval"), ("Disposed", "Disposed"), ("Sold", "Sold"), ("Expired", "Expired"), ("Lost", "Lost")];
    public static string Label(string code) => All.FirstOrDefault(s => s.Code == code).Label ?? code;
}

/// <summary>A field that depends on the asset type (form 01 Asset Registration).</summary>
public sealed record ItamField(string Code, string Label, string Kind, bool Required, string? List = null);

public static class ItamTypeFields
{
    static readonly Dictionary<string, ItamField[]> Fields = new()
    {
        ["PC"] = [new("ComputerType", "Computer type", "list", true, "ComputerType"), new("Processor", "Processor", "text", false),
            new("CapacityGb", "Storage (GB)", "number", false), new("MemoryGb", "Memory (GB)", "number", false),
            new("OperatingSystem", "Operating system", "list", false, "OperatingSystem"), new("ComputerName", "Computer name", "text", false)],
        ["PNT"] = [new("PrinterType", "Printer type", "list", true, "PrinterType"), new("PrinterLocation", "Printer location", "text", false),
            new("TonerType", "Toner type", "list", false, "TonerType")],
        ["IP"] = [new("Extension", "Extension number", "text", false)],
        ["SOF"] = [new("Software", "Software name", "text", true)],
        ["TON"] = [new("TonerCode", "Toner code", "text", true)],
        ["DEV"] = [new("DeviceName", "Device name", "text", true), new("DeviceDescription", "Device description", "text", false)]
    };
    public static ItamField[] For(string type) => Fields.TryGetValue(type, out var f) ? f : [];
    /// <summary>Serial number: not asked for software and IP phones; optional for toner; required for every other type.
    /// Service tag: required for computers. MAC: IP phones only (required; it identifies the phone). IP address: printers and IP phones.</summary>
    public static bool SerialUsed(string type) => type is not ("SOF" or "IP");
    /// <summary>Manufacturer: every type except software (the software name identifies it). Model: not for software or toner
    /// (the toner code identifies the cartridge).</summary>
    public static bool ManufacturerUsed(string type) => type != "SOF";
    public static bool ModelUsed(string type) => type is not ("SOF" or "TON");
    public static bool SerialRequired(string type) => SerialUsed(type) && type != "TON";
    public static bool ServiceTagUsed(string type) => type == "PC";
    public static bool MacUsed(string type) => type == "IP";
    public static bool MacRequired(string type) => type == "IP";
    public static bool IpUsed(string type) => type is "PNT" or "IP";
    public static bool KeyUsed(string type) => type is "PC" or "SOF";
}

/// <summary>What the signed-in person may do, from their IT roles (Role Permissions workbook; blank cells grant nothing).</summary>
public sealed record ItamScope(Guid Id, string Name, string[] Roles, string[] Regions)
{
    bool Has(string role) => Roles.Contains(role);
    public bool Specialist => Has(ItamRoles.SupportSpecialist);
    public bool TeamLeader => Has(ItamRoles.TeamLeader);
    public bool SysAdmin => Has(ItamRoles.SystemAdministrator);
    public bool Voice => Has(ItamRoles.VoiceAdministrator);
    public bool Officer => Has(ItamRoles.AssetsOfficer);
    public bool Manager => Has(ItamRoles.ItManager);
    public bool ItAdmin => Has(ItamRoles.ItAdministrator);
    public bool Auditor => Has(ItamRoles.Auditor);
    public bool HasItRole => Roles.Length > 0;

    /// <summary>View assets: own regions (specialist); all (Team Leader, Assets Officer, IT Manager, IT Administrator,
    /// Auditor); computers only (System Administrator); IP phones only (Voice Administrator).</summary>
    public bool ViewsAll => TeamLeader || Officer || Manager || ItAdmin || Auditor;
    public bool CanView(string type, string region) => ViewsAll || (Specialist && Regions.Contains(region)) || (SysAdmin && type == "PC") || (Voice && type == "IP");
    public bool CanViewAny => ViewsAll || Specialist || SysAdmin || Voice;
    /// <summary>Register an asset: own regions (specialist); yes for the other IT roles except the Auditor.</summary>
    public bool CanRegister(string region) => TeamLeader || SysAdmin || Voice || Officer || Manager || ItAdmin || (Specialist && Regions.Contains(region));
    public bool CanRegisterAny => TeamLeader || SysAdmin || Voice || Officer || Manager || ItAdmin || Specialist;
    /// <summary>Edit asset details: own regions (specialist); all (Team Leader, Assets Officer, IT Administrator); IP phones
    /// MAC, IP address and extension only (Voice Administrator).</summary>
    public bool CanEdit(string type, string region) => TeamLeader || Officer || ItAdmin || (Specialist && Regions.Contains(region));
    public bool CanEditPhoneOnly(string type) => Voice && type == "IP";
    /// <summary>OS and software keys: IT Administrator and IT Assets Officer; every view logged.</summary>
    public bool CanSeeKeys => ItAdmin || Officer;
    /// <summary>Record a PO and receive its devices: Team Leader and IT Assets Officer.</summary>
    public bool PoManage => TeamLeader || Officer;
    /// <summary>View POs and print the PO delivery report: Team Leader, System Administrator, Assets Officer, IT Manager, Auditor.</summary>
    public bool PoView => TeamLeader || SysAdmin || Officer || Manager || Auditor;
    public bool RolesManage => Manager || ItAdmin;
    public bool MasterData => ItAdmin;
    public bool AuditView => Manager || ItAdmin || Auditor;
    public bool BackupsAll => Manager || ItAdmin;
    public bool BackupsView => Manager || ItAdmin || Auditor;
    public bool OwnBackup => HasItRole && !(Roles.Length == 1 && Auditor);
}
