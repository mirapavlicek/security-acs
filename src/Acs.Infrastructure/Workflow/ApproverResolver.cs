using Acs.Domain.Entities;
using Acs.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Acs.Infrastructure.Workflow;

/// <summary>Odkud se schvalovatel vzal.</summary>
public enum ApproverOrigin
{
    /// <summary>Konkrétní uživatel uvedený v matici.</summary>
    User = 0,
    /// <summary>Nadřízený cílového zaměstnance podle <see cref="Employee.ManagerId"/>.</summary>
    LineManager = 1,
}

/// <summary>
/// Vyhodnocený schvalovatel jedné úrovně pro konkrétní žádost.
/// <paramref name="UserId"/> je null, když nadřízený ještě nemá účet v ACS
/// (nepřihlásil se) — pak se pozná podle <paramref name="EmployeeId"/> nebo AD účtu
/// a e-mail se bere ze záznamu zaměstnance.
/// </summary>
public record ResolvedApprover(
    int? UserId,
    int? EmployeeId,
    string DisplayName,
    string? Email,
    ApproverOrigin Origin,
    int ManagerDepth = 0,
    string? AdAccount = null)
{
    /// <summary>Značka pro zobrazení („nadřízený“, „nadřízený nadřízeného“).</summary>
    public string OriginLabel => Origin switch
    {
        ApproverOrigin.LineManager when ManagerDepth >= 2 => "nadřízený nadřízeného",
        ApproverOrigin.LineManager => "nadřízený",
        _ => "schvalovatel",
    };

    /// <summary>Jedná daná identita (uživatel + zástupy) za tohoto schvalovatele?</summary>
    public bool Matches(ActingIdentity identity)
        => (UserId is { } uid && identity.UserIds.Contains(uid))
           || (EmployeeId is { } eid && identity.EmployeeIds.Contains(eid))
           || (AdAccount is { } acc && identity.UserName is { } name
               && string.Equals(acc, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Za koho uživatel právě jedná: vlastní účet + platné zástupy, a odpovídající
/// zaměstnanci (kvůli schvalovatelům určeným nadřízeností, kteří nemusí mít účet).
/// </summary>
public record ActingIdentity(
    int UserId, HashSet<int> UserIds, HashSet<int> EmployeeIds, string? UserName, HashSet<int> OwnEmployeeIds)
{
    /// <summary>Jen vlastní identita bez zástupů — pro rozlišení „sám za sebe“ vs. „v zástupu“.</summary>
    public ActingIdentity SelfOnly() => new(UserId, [UserId], OwnEmployeeIds, UserName, OwnEmployeeIds);
}

/// <summary>Výsledek vyhodnocení úrovně matice pro položku žádosti.</summary>
public record LevelResolution(
    ApprovalLevel Level,
    IReadOnlyList<ResolvedApprover> Approvers,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Úroveň obsahuje schvalovatele typu „nadřízený“.</summary>
    public bool HasManagerApprovers => Level.Approvers.Any(a => a.Kind == ApproverKind.LineManager);

    /// <summary>
    /// Po vyhodnocení nezůstal nikdo, kdo by mohl rozhodnout (např. zaměstnanec bez
    /// nadřízeného) — položku na této úrovni rozhoduje administrátor.
    /// </summary>
    public bool RequiresAdminFallback => Approvers.Count == 0;

    /// <summary>Uživatelé (id) rozhodující na úrovni — pro počty a notifikace.</summary>
    public IEnumerable<int> UserIds => Approvers.Where(a => a.UserId != null).Select(a => a.UserId!.Value).Distinct();
}

/// <summary>
/// Převádí definici úrovně matice (konkrétní uživatelé, nadřízený zaměstnance) na
/// seznam lidí, kteří u dané žádosti smějí rozhodnout. Nadřízený se dohledává
/// z <see cref="Employee.ManagerId"/> cílového zaměstnance žádosti:
/// <list type="bullet">
///   <item>hloubka 1 = přímý nadřízený, 2 = nadřízený nadřízeného,</item>
///   <item><b>samoschválení</b> — je-li nalezený nadřízený sám cílový zaměstnanec nebo
///     žadatel, jde se o úroveň výš (ředitel nemůže schvalovat sám sobě),</item>
///   <item>nadřízený bez účtu v ACS se identifikuje přes <c>EmployeeId</c> / AD účet —
///     po prvním přihlášení se spáruje stávajícím mechanismem,</item>
///   <item>bez nadřízeného → úroveň zůstane bez schvalovatele a rozhoduje administrátor
///     (<see cref="LevelResolution.RequiresAdminFallback"/>).</item>
/// </list>
/// Instance si během jednoho požadavku pamatuje načtené zaměstnance a uživatele,
/// takže vyhodnocení desítek položek nevede k desítkám stejných dotazů.
/// </summary>
public class ApproverResolver(AcsDbContext db)
{
    /// <summary>Pojistka proti cyklu v nadřízenosti (A → B → A).</summary>
    private const int MaxChainLength = 20;

    private readonly Dictionary<int, Employee?> _employees = [];
    private readonly Dictionary<int, AppUser?> _users = [];
    private readonly Dictionary<int, AppUser?> _usersByEmployee = [];

    /// <summary>Identita uživatele včetně zástupů a navázaných zaměstnanců.</summary>
    public async Task<ActingIdentity> GetActingIdentityAsync(int userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var principals = await db.Deputies
            .Where(d => d.DeputyUserId == userId && d.ValidFrom <= now && now <= d.ValidTo)
            .Select(d => d.PrincipalUserId)
            .ToListAsync(ct);
        var userIds = new HashSet<int> { userId };
        userIds.UnionWith(principals);

        var users = await db.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.EmployeeId, u.UserName })
            .ToListAsync(ct);
        var employeeIds = users.Where(u => u.EmployeeId != null).Select(u => u.EmployeeId!.Value).ToHashSet();
        var self = users.FirstOrDefault(u => u.Id == userId);
        var userName = self?.UserName;
        var ownEmployeeIds = new HashSet<int>();
        if (self?.EmployeeId is { } ownId)
            ownEmployeeIds.Add(ownId);

        // Uživatel bez spárovaného zaměstnance, ale se stejným AD účtem — spárování
        // proběhne při příštím importu; do té doby stačí shoda účtu.
        if (userName is not null && ownEmployeeIds.Count == 0)
        {
            var byAccount = await db.Employees
                .Where(e => e.AdAccount != null && e.AdAccount == userName)
                .Select(e => e.Id)
                .ToListAsync(ct);
            ownEmployeeIds.UnionWith(byAccount);
        }
        employeeIds.UnionWith(ownEmployeeIds);

        return new ActingIdentity(userId, userIds, employeeIds, userName, ownEmployeeIds);
    }

    /// <summary>Vyhodnotí úroveň pro položku; <c>item.Request</c> musí být načtený (stačí id).</summary>
    public async Task<LevelResolution> ResolveAsync(
        ApprovalLevel level, AccessRequestItem item, CancellationToken ct = default)
    {
        var request = item.Request
            ?? await db.AccessRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == item.RequestId, ct)
            ?? throw new InvalidOperationException("Žádost položky nenalezena.");
        return await ResolveAsync(level, request.TargetEmployeeId, request.RequesterUserId, ct);
    }

    /// <summary>Vyhodnotí úroveň pro cílového zaměstnance a žadatele.</summary>
    public async Task<LevelResolution> ResolveAsync(
        ApprovalLevel level, int targetEmployeeId, int requesterUserId, CancellationToken ct = default)
    {
        var approvers = new List<ResolvedApprover>();
        var warnings = new List<string>();
        var seenUsers = new HashSet<int>();
        var seenEmployees = new HashSet<int>();

        foreach (var approver in level.Approvers.OrderBy(a => a.Id))
        {
            switch (approver.Kind)
            {
                case ApproverKind.User when approver.UserId is { } userId:
                {
                    var user = await GetUserAsync(userId, ct);
                    if (user is null || !seenUsers.Add(user.Id))
                        continue;
                    if (user.EmployeeId is { } eid)
                        seenEmployees.Add(eid);
                    approvers.Add(new ResolvedApprover(
                        user.Id, user.EmployeeId, user.DisplayName ?? user.UserName, user.Email,
                        ApproverOrigin.User, AdAccount: user.UserName));
                    break;
                }

                case ApproverKind.LineManager:
                {
                    var depth = Math.Max(1, approver.ManagerDepth);
                    var (manager, warning) = await FindManagerAsync(targetEmployeeId, requesterUserId, depth, ct);
                    if (manager is null)
                    {
                        warnings.Add(warning ?? "nadřízený nenalezen");
                        continue;
                    }

                    var user = await GetUserByEmployeeAsync(manager, ct);
                    if (user is not null && !seenUsers.Add(user.Id))
                        continue;
                    if (!seenEmployees.Add(manager.Id))
                        continue;
                    if (user?.EmployeeId is { } eid)
                        seenEmployees.Add(eid);

                    approvers.Add(new ResolvedApprover(
                        user?.Id, manager.Id,
                        user?.DisplayName ?? manager.FullName,
                        user?.Email ?? manager.Email,
                        ApproverOrigin.LineManager, depth, manager.AdAccount ?? user?.UserName));
                    if (user is null)
                        warnings.Add($"{manager.FullName} se do ACS ještě nepřihlásil — rozhodne po prvním přihlášení.");
                    break;
                }

                // AD skupiny se ve workflow zatím nevyhodnocují (stejně jako dosud).
            }
        }

        return new LevelResolution(level, approvers, warnings);
    }

    /// <summary>
    /// Nadřízený v požadované hloubce s přeskočením samoschválení. Vrací i důvod,
    /// proč se nenašel — zobrazuje se v detailu žádosti.
    /// </summary>
    public async Task<(Employee? Manager, string? Warning)> FindManagerAsync(
        int targetEmployeeId, int? requesterUserId, int depth, CancellationToken ct = default)
    {
        var target = await GetEmployeeAsync(targetEmployeeId, ct);
        if (target is null)
            return (null, "cílový zaměstnanec nenalezen");

        var requester = requesterUserId is { } rid ? await GetUserAsync(rid, ct) : null;
        bool IsRequester(Employee e)
            => requester is not null
               && (e.Id == requester.EmployeeId
                   || (e.AdAccount is { } acc && !requester.IsLocal
                       && string.Equals(acc, requester.UserName, StringComparison.OrdinalIgnoreCase)));

        var visited = new HashSet<int> { target.Id };
        var current = target;
        var reached = 0;
        while (reached < depth)
        {
            if (current.ManagerId is not { } managerId)
            {
                return (null, reached == 0
                    ? $"{target.FullName} nemá v ACS nadřízeného"
                    : $"{current.FullName} (nadřízený {reached}. stupně) nemá nadřízeného");
            }

            var next = await GetEmployeeAsync(managerId, ct);
            if (next is null || !visited.Add(next.Id) || visited.Count > MaxChainLength)
                return (null, "řetěz nadřízených je přerušený nebo cyklický");
            if (!next.IsActive)
                return (null, $"nadřízený {next.FullName} není aktivní zaměstnanec");

            current = next;
            reached++;
        }

        // Samoschválení: nadřízený je sám cílový zaměstnanec (nemůže nastat kvůli visited)
        // nebo žadatel — jde se o úroveň výš, dokud je kam.
        while (IsRequester(current))
        {
            if (current.ManagerId is not { } upId)
                return (null, $"{current.FullName} je zároveň žadatelem a nemá dalšího nadřízeného");
            var up = await GetEmployeeAsync(upId, ct);
            if (up is null || !visited.Add(up.Id) || visited.Count > MaxChainLength)
                return (null, "řetěz nadřízených je přerušený nebo cyklický");
            if (!up.IsActive)
                return (null, $"nadřízený {up.FullName} není aktivní zaměstnanec");
            current = up;
        }

        return (current, null);
    }

    private async Task<Employee?> GetEmployeeAsync(int id, CancellationToken ct)
    {
        if (_employees.TryGetValue(id, out var cached))
            return cached;
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        _employees[id] = employee;
        return employee;
    }

    private async Task<AppUser?> GetUserAsync(int id, CancellationToken ct)
    {
        if (_users.TryGetValue(id, out var cached))
            return cached;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        _users[id] = user;
        return user;
    }

    /// <summary>Účet zaměstnance — přednostně spárovaný, jinak podle AD účtu.</summary>
    private async Task<AppUser?> GetUserByEmployeeAsync(Employee employee, CancellationToken ct)
    {
        if (_usersByEmployee.TryGetValue(employee.Id, out var cached))
            return cached;

        var user = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.EmployeeId == employee.Id)
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(ct);
        if (user is null && employee.AdAccount is { } account)
        {
            user = await db.Users.AsNoTracking()
                .Where(u => u.IsActive && !u.IsLocal && u.UserName == account)
                .OrderBy(u => u.Id)
                .FirstOrDefaultAsync(ct);
        }

        _usersByEmployee[employee.Id] = user;
        if (user is not null)
            _users[user.Id] = user;
        return user;
    }
}
