using Microsoft.AspNetCore.Http;

namespace Itam.App;

/// <summary>The signed-in Windows account (production: Windows sign-in; demo: chosen person), keyed by the stable
/// Active Directory objectGUID.</summary>
public sealed record ItamUser(Guid Id, string Account);

/// <summary>One employee as read from the Active Directory users OU (read-only), with the same attributes as VTMS: region postalCode, branch
/// physicalDeliveryOfficeName, job title description, company, department, HR code postOfficeBox, mobile, manager, SAP ID streetAddress.</summary>
public sealed record ItamEmployee(Guid Id, string Account, string Name, string? Email, string? JobTitle, string? Department, Guid? ManagerId, bool Enabled,
    string? Mobile = null, string? Branch = null, string? Company = null, string? HrCode = null, string? Region = null, string? SapId = null, string? IpPhone = null);

public interface IItamSession { Task<ItamUser?> Current(HttpContext context, CancellationToken token); }

/// <summary>Encrypts OS and software keys (production: the site's data-protection keys).</summary>
public interface IItamSecrets { string Protect(string value); string Unprotect(string value); }

public sealed class ItamAccessDenied(string message) : Exception(message);
public sealed class ItamConflict(string message) : Exception(message);

public static class ItamRoles
{
    // The IT Administrator and Auditor roles were removed (owner, 22 October 2026): the IT Manager gives roles, keeps master
    // data and reads the audit log.
    public const string SupportSpecialist = "SupportSpecialist", TeamLeader = "TeamLeader", SystemAdministrator = "SystemAdministrator",
        VoiceAdministrator = "VoiceAdministrator", AssetsOfficer = "AssetsOfficer", ItManager = "ItManager", InkOfficer = "InkOfficer";
    public static readonly string[] All = [SupportSpecialist, TeamLeader, SystemAdministrator, VoiceAdministrator, AssetsOfficer, ItManager, InkOfficer];
    /// <summary>Roles given per region (one row per region).</summary>
    public static bool PerRegion(string role) => role is SupportSpecialist or InkOfficer;
    public static string Label(string code) => code switch
    {
        SupportSpecialist => "IT Support Specialist", TeamLeader => "IT Helpdesk Team Leader", SystemAdministrator => "System Administrator",
        VoiceAdministrator => "Voice Administrator", AssetsOfficer => "IT Assets Officer", ItManager => "IT Manager", InkOfficer => "Ink Officer", _ => code
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
        ["PC"] = [new("ComputerType", "Computer type", "list", true, "ComputerType"), new("Processor", "Processor", "text", true),
            new("CapacityGb", "Storage (GB)", "number", true), new("MemoryGb", "Memory (GB)", "number", true),
            new("OperatingSystem", "Operating system", "list", true, "OperatingSystem"), new("ComputerName", "Computer name", "text", false)],
        ["PNT"] = [new("PrinterType", "Printer type", "list", false, "PrinterType"), new("PrinterLocation", "Printer location", "text", false),
            new("TonerType", "Toner type", "list", false, "TonerType"), new("TonerCode", "Toner code", "text", false)],
        ["IP"] = [new("Extension", "Extension number", "text", false)],
        ["SOF"] = [new("Software", "Software name", "text", true), new("Seats", "Seats (users)", "number", false)],
        ["TON"] = [new("TonerCode", "Toner code", "text", true)],
        ["DEV"] = [new("DeviceName", "Device name", "text", true), new("DeviceDescription", "Device description", "text", false)]
    };
    public static ItamField[] For(string type) => Fields.TryGetValue(type, out var f) ? f : [];
    /// <summary>Serial number: not asked for computers (the service tag identifies them), software and IP phones; optional for
    /// toner; required for every other type.
    /// Service tag: required for computers. MAC: IP phones only (required; it identifies the phone). IP address: printers and IP phones.</summary>
    public static bool SerialUsed(string type) => type is not ("PC" or "SOF" or "IP");
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

/// <summary>What the signed-in person may do, from their IT roles: the owner's permission matrix of 22 October 2026
/// (IT Assets - Permission Matrix). Blank cells grant nothing; a person with several roles has all their rights.</summary>
public sealed record ItamScope(Guid Id, string Name, string[] Roles, string[] Regions, string[] InkRegions)
{
    bool Has(string role) => Roles.Contains(role);
    public bool Specialist => Has(ItamRoles.SupportSpecialist);
    public bool TeamLeader => Has(ItamRoles.TeamLeader);
    public bool SysAdmin => Has(ItamRoles.SystemAdministrator);
    public bool Voice => Has(ItamRoles.VoiceAdministrator);
    public bool Officer => Has(ItamRoles.AssetsOfficer);
    public bool Manager => Has(ItamRoles.ItManager);
    public bool InkOfficer => InkRegions.Length > 0;
    public bool HasItRole => Roles.Length > 0;
    bool Mine(string? region) => Specialist && region is not null && Regions.Contains(region);
    /// <summary>The roles with rights in every region: Team Leader, System Administrator, IT Assets Officer, IT Manager.</summary>
    bool Wide => TeamLeader || SysAdmin || Officer || Manager;

    // Assets
    public bool ViewsAll => Wide;
    public bool CanView(string type, string region) => Wide || Mine(region) || (Voice && type == "IP");
    public bool CanViewAny => Wide || Specialist || Voice;
    public bool CanRegister(string region) => Wide || Voice || Mine(region);
    public bool CanRegisterAny => Wide || Voice || Specialist;
    public bool CanEdit(string type, string region) => Wide || Mine(region);
    public bool CanEditPhoneOnly(string type) => Voice && type == "IP";
    /// <summary>OS and software keys (every view logged): System Administrator, IT Assets Officer, IT Manager; specialists in their regions.</summary>
    public bool CanSeeKeys(string region) => SysAdmin || Officer || Manager || Mine(region);
    public bool CanPrintLabel(string region) => Wide || Mine(region) || InkRegions.Contains(region);

    // Purchase orders: specialists record POs too, and receive devices into their regions only.
    public bool PoManage => Wide || Specialist;
    public bool PoView => Wide || Specialist;
    public bool CanReceiveInto(string region) => Wide || Mine(region);

    // Handover (delivery, signed copy, receipt, technical assessment) and repairs done by IT
    public bool CanHandover(string region) => Wide || Mine(region);
    public bool CanHandoverAny => Wide || Specialist;
    public bool HandoverView => Wide || Specialist || Voice;

    // Infrastructure tasks after a return
    public bool AdTasks => TeamLeader || SysAdmin;
    public bool CucmTasks => SysAdmin || Voice;
    public bool TasksView => Wide || Voice;

    // Ink
    /// <summary>Ask for ink for another employee (others: their own team as the direct manager).</summary>
    public bool InkForOthers => Specialist || TeamLeader || Officer || InkOfficer;
    /// <summary>Supplier order, delivery and cancelling: System Administrator, IT Assets Officer, IT Manager; Ink Officer in their regions.</summary>
    public bool CanInk(string? region) => SysAdmin || Officer || Manager || (region is null ? InkOfficer : InkRegions.Contains(region));
    public bool CanInkAny => SysAdmin || Officer || Manager || InkOfficer;
    /// <summary>Send an ink request for approval: also the Team Leader.</summary>
    public bool CanInkSend(string? region) => CanInk(region) || TeamLeader;
    /// <summary>Link or register a printer not in IT Assets: also the IT Support Specialists.</summary>
    public bool CanInkPrinter(string? region) => CanInkSend(region) || Specialist;
    public bool InkView => CanInkAny || TeamLeader;
    public bool InkHandle => CanInkAny || TeamLeader || Specialist;

    // Invoices, payment packs and suppliers
    public bool InvoicesView => Wide || Specialist || InkOfficer;
    public bool InvoiceToners => SysAdmin || Officer || Manager || InkOfficer;
    public bool InvoiceDevices => Wide;
    public bool InvoiceParts => Wide || Specialist;
    public bool SuppliersManage => Wide || Specialist || InkOfficer;

    // Maintenance orders: create and follow; approve (Team Leader, System Administrator, IT Manager; never their own order)
    public bool MaintenanceManage => Wide || Specialist;
    public bool MaintenanceApproverRole => TeamLeader || SysAdmin || Manager;
    /// <summary>Gate passes: approve and resolve discrepancies (same three roles as maintenance; never their own pass).</summary>
    public bool MovementApproverRole => TeamLeader || SysAdmin || Manager;
    public bool MaintenanceView => Wide || Specialist;
    public bool ItRepair(string region) => CanHandover(region);

    // End of life (step 5). Decisions not yet in the owner's matrix are given to the IT Manager (or a backup on leave) and
    // listed in the build plan to confirm: the loss decision, the write-off approval and the disposal approval.
    /// <summary>Report a loss: IT of the asset's region (the holder reports their own assets too).</summary>
    public bool LossReport(string region) => CanHandover(region);
    public bool LossView(string region) => Wide || Mine(region);
    public bool LossViewAny => Wide || Specialist;
    public bool LossAssign => Manager;
    public bool LossDecideRole => Manager;
    public bool LossSecurity => SysAdmin || TeamLeader || Manager;
    public bool WriteOffRequest => Officer || Manager;
    public bool DisposalRequest(string region) => CanHandover(region);
    public bool DisposalApproverRole => Manager;
    public bool DisposalComplete => Officer || Manager;
    public bool DisposalView => Wide || Specialist;
    /// <summary>Any IT role records a sale (decided 20 October 2026).</summary>
    public bool SaleRecord => HasItRole;
    public bool SalesView => HasItRole;
    /// <summary>Only the System Administrator marks an employee Resigned; the System Administrator or the IT Manager completes the clearance.</summary>
    public bool MarkResigned => SysAdmin;
    public bool ClearanceComplete => SysAdmin || Manager;
    public bool UserDisableTask => SysAdmin;
    public bool CollectView => Wide || Specialist;
    public bool CollectRegion(string region) => Wide || Mine(region);
    public bool AdCompareRun => SysAdmin || Officer || Manager;
    public bool AdCompareView => Wide || Specialist;
    public bool AdCompareReview => SysAdmin || Officer || Manager;
    public bool LicensesView => SysAdmin || Officer || Manager || Specialist;

    // Administration and backups
    public bool RolesManage => Manager;
    public bool MasterData => Manager;
    public bool AuditView => Manager;
    public bool BackupsAll => TeamLeader || SysAdmin || Manager;
    public bool BackupsView => BackupsAll;
    public bool OwnBackup => HasItRole;
}
