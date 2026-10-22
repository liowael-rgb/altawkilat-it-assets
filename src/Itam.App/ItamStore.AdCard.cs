using System.Data.Common;

namespace Itam.App;

/// <summary>The requester panel (owner, 22 October 2026): every request page shows, on the right, all the information
/// imported from Active Directory about the employee (the same attributes as VTMS; the national ID is never read).</summary>
public sealed partial class ItamStore
{
    internal static async Task<object?> EmployeeAd(DbConnection c, DbTransaction? tx, Guid? employeeId, CancellationToken token)
    {
        if (employeeId is not Guid id) return null;
        return (await Rows(c, tx, "SELECT e.display_name,e.account,e.email,e.job_title,e.department,e.company,e.branch,e.region,e.hr_code,e.sap_id,e.mobile,e.ip_phone," +
            "m.display_name,m.job_title,e.enabled,e.synced_at_utc FROM itam_access.employee e LEFT JOIN itam_access.employee m ON m.employee_id=e.manager_id WHERE e.employee_id=@e",
            r => new
            {
                name = r.GetString(0), account = r.GetString(1), email = S(r, 2), jobTitle = S(r, 3), department = S(r, 4), company = S(r, 5), branch = S(r, 6),
                region = S(r, 7), hrCode = S(r, 8), sapId = S(r, 9), mobile = S(r, 10), ipPhone = S(r, 11), manager = S(r, 12), managerTitle = S(r, 13),
                enabled = r.GetBoolean(14), syncedAt = Utc(r, 15)
            }, token, ("@e", id))).SingleOrDefault();
    }
}
