namespace Itam.App;

/// <summary>What the production host needs from the store: the signed-in employee by account, and the size of the AD copy (a
/// directory read that would disable more than half of the enabled employees is refused as a wrong OU or a partial read).</summary>
public sealed partial class ItamStore
{
    public async Task<Guid?> EmployeeByAccount(string account, CancellationToken token)
    {
        await using var c = await Open(token);
        return await Scalar(c, null, "SELECT employee_id FROM itam_access.employee WHERE account=@a AND enabled=1", token, ("@a", account)) as Guid?;
    }

    public async Task<int> EnabledEmployees(CancellationToken token)
    {
        await using var c = await Open(token);
        return Convert.ToInt32(await Scalar(c, null, "SELECT COUNT(*) FROM itam_access.employee WHERE enabled=1", token));
    }
}
