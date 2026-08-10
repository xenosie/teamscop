using Teamscop.Api.Data;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;

namespace Teamscop.Api.Services.Access;

/// <summary>
/// Everything the authorization rules need about one principal, loaded once per request.
/// Every question below is then a pure function — no database, no async, no ordering hazard.
/// </summary>
/// <param name="GrantedPackages">
/// Raw rows from the policeman grant table. Only meaningful while <see cref="IsPoliceman"/>;
/// use <see cref="Packages"/> for the effective set.
/// </param>
/// <param name="LedTeamId">The team this user leads, if any. A leader leads exactly one team.</param>
public sealed record ViewerContext(
    Guid UserId,
    Guid CompanyId,
    UserRole Role,
    bool IsPoliceman,
    long AuthorityVersion,
    IReadOnlySet<string> GrantedPackages,
    Guid? LedTeamId,
    IReadOnlySet<Guid> LedMemberIds)
{
    /// <summary>
    /// Team-scoped views a leader gets by leading, without any grant (§4.2).
    ///
    /// Timetrack and screenshots, by the owner's decision: a leader may see whether their team is
    /// working and what is on their screens. Browsing history is NOT inherent — it stays behind an
    /// explicit policeman grant even for the leader's own team. All of this is scoped to the led
    /// team; company-wide reach still requires a granted package (see CanViewEventTypeFor).
    /// </summary>
    private static readonly string[] InherentLeaderPackages =
    [
        AuthorityPackageIds.ViewTimeTrack,
        AuthorityPackageIds.ViewScreenshot
    ];

    /// <summary>
    /// Holding any one of these lets a policeman SEE THE ROSTER company-wide (§4.3) — they must be
    /// able to pick any employee to issue an approval code for. It does not follow that they may
    /// read that employee's data; that is <see cref="CompanyWideDataPackages"/>.
    /// </summary>
    private static readonly HashSet<string> CompanyWideRosterPackages = new(StringComparer.Ordinal)
    {
        AuthorityPackageIds.ViewScreenshot,
        AuthorityPackageIds.ViewTimeTrack,
        AuthorityPackageIds.ViewBrowserHistory,
        AuthorityPackageIds.UsbApproval,
        AuthorityPackageIds.UninstallApproval
    };

    /// <summary>
    /// Holding any one of these — GRANTED, not inherited by leading a team — widens tracking data
    /// to the whole company. The approval packages are deliberately absent: issuing USB codes is a
    /// routine delegation and must not carry sight of anyone's screen.
    /// </summary>
    private static readonly HashSet<string> CompanyWideDataPackages = new(StringComparer.Ordinal)
    {
        AuthorityPackageIds.ViewScreenshot,
        AuthorityPackageIds.ViewTimeTrack,
        AuthorityPackageIds.ViewBrowserHistory
    };

    /// <summary>Every event type a viewer can be granted sight of.</summary>
    public static readonly IReadOnlyList<string> KnownEventTypes =
    [
        AgentEventTypes.Heartbeat, AgentEventTypes.Connectivity, AgentEventTypes.TimeTrack,
        AgentEventTypes.ScreenshotMeta, AgentEventTypes.BrowserHistory, AgentEventTypes.UsbEvent,
        AgentEventTypes.Registration, AgentEventTypes.PowerOff, AgentEventTypes.Uninstall,
        AgentEventTypes.AppStatus, AgentEventTypes.Install
    ];

    private readonly HashSet<string> _effective =
        BuildEffective(Role, IsPoliceman, GrantedPackages, LedTeamId is not null);

    public bool IsAdmin => Role == UserRole.Admin;

    public bool IsTeamLeader => LedTeamId is not null;

    /// <summary>Granted packages plus a leader's inherent views. Admins hold everything.</summary>
    public IReadOnlySet<string> Packages => _effective;

    /// <summary>Effective hold — includes a team leader's inherent views.</summary>
    public bool Has(string packageId)
        => IsAdmin || _effective.Contains(AuthorityPackageIds.Normalize(packageId));

    /// <summary>
    /// Granted via the policeman table (or admin) only. A team leader must never inherit
    /// team management or TOTP generation by virtue of leading a team.
    /// </summary>
    public bool HasGranted(string packageId)
        => IsAdmin || (IsPoliceman && GrantedPackages.Contains(AuthorityPackageIds.Normalize(packageId)));

    public bool CanManageTeams => HasGranted(AuthorityPackageIds.TeamManagement);

    public bool CanGenerateTotp
        => IsAdmin
           || (IsPoliceman
               && (GrantedPackages.Contains(AuthorityPackageIds.UsbApproval)
                   || GrantedPackages.Contains(AuthorityPackageIds.UninstallApproval)));

    /// <summary>
    /// May list every employee in the company — the staff picker and the Codes screen. Includes
    /// approval-only policemen, who need to choose whom to issue a code for.
    /// </summary>
    public bool CanViewCompanyStaff
        => IsAdmin || (IsPoliceman && GrantedPackages.Overlaps(CompanyWideRosterPackages));

    /// <summary>
    /// May read TRACKING DATA for every employee, not just a led team.
    ///
    /// Deliberately narrower than <see cref="CanViewCompanyStaff"/> and deliberately keyed on
    /// GRANTED packages. A team leader carries inherent view packages that §4.2 scopes to their own
    /// team; if company-wide reach were decided by the effective set, granting that leader
    /// <c>usb_approval</c> — a routine, low-privilege delegation — would silently promote those
    /// inherent views to the whole company and hand them every employee's screenshots and browsing.
    /// </summary>
    public bool CanViewCompanyData
        => IsAdmin || (IsPoliceman && GrantedPackages.Overlaps(CompanyWideDataPackages));

    /// <summary>Whether this viewer may see <paramref name="target"/>'s data at all (§4.5, tenant isolation).</summary>
    public bool CanView(ViewerContext target)
        => CanViewStaff(target.UserId, target.CompanyId, target.Role);

    /// <summary>
    /// The same rule as <see cref="CanView(ViewerContext)"/>, asked of an identity rather than a
    /// fully loaded principal. The avatar route knows its owner's id, company and role already and
    /// must not load a second <see cref="ViewerContext"/> per image (B12).
    /// </summary>
    public bool CanViewStaff(Guid staffUserId, Guid staffCompanyId, UserRole staffRole)
    {
        // Nobody monitors themselves. The sticker's own-timetrack path is the one exception
        // and is handled by IStaffDataGuard's allowSelf, never here.
        if (UserId == staffUserId)
        {
            return false;
        }

        if (CompanyId != staffCompanyId || staffRole != UserRole.Staff)
        {
            return false;
        }

        return IsAdmin || CanViewCompanyData || LedMemberIds.Contains(staffUserId);
    }

    public bool CanViewEventType(string eventType)
    {
        if (IsAdmin)
        {
            return true;
        }

        return TypeAllowedBy(eventType, pkg => _effective.Contains(pkg));
    }

    /// <summary>
    /// Whether this viewer may read <paramref name="eventType"/> ABOUT <paramref name="staffUserId"/>.
    ///
    /// The flat check above collapses two scopes into one set, and that collapse was an escalation
    /// for anyone who is BOTH a team leader and a policeman: the leader's inherent timetrack is
    /// team-scoped, but once any granted view package widened CanViewStaff to the whole company,
    /// the flat set answered "yes, timetrack" for every employee in it. Here the two sources are
    /// kept apart — granted packages reach company-wide, inherent leader packages reach exactly the
    /// led team — so each event type travels only as far as the package that justifies it.
    /// </summary>
    public bool CanViewEventTypeFor(string eventType, Guid staffUserId)
    {
        if (IsAdmin)
        {
            return true;
        }

        if (IsPoliceman && TypeAllowedBy(eventType, pkg => GrantedPackages.Contains(pkg)))
        {
            return true;
        }

        return LedMemberIds.Contains(staffUserId)
               && TypeAllowedBy(eventType, pkg => InherentLeaderPackages.Contains(pkg));
    }

    private static bool TypeAllowedBy(string eventType, Func<string, bool> holds) => eventType switch
    {
        AgentEventTypes.ScreenshotMeta => holds(AuthorityPackageIds.ViewScreenshot),
        AgentEventTypes.TimeTrack => holds(AuthorityPackageIds.ViewTimeTrack),
        AgentEventTypes.BrowserHistory => holds(AuthorityPackageIds.ViewBrowserHistory),
        AgentEventTypes.UsbEvent => holds(AuthorityPackageIds.UsbApproval),
        // Phase 9 lifecycle history — never inherent for team leaders.
        AgentEventTypes.Registration or AgentEventTypes.PowerOff or AgentEventTypes.Uninstall
            or AgentEventTypes.AppStatus or AgentEventTypes.Install
            => holds(AuthorityPackageIds.UninstallApproval),
        AgentEventTypes.Heartbeat or AgentEventTypes.Connectivity
            => InherentLeaderPackages.Any(holds) || CompanyWideDataPackages.Any(holds),
        _ => false
    };

    /// <summary>The event types this viewer may read when no explicit type is requested.</summary>
    public HashSet<string> ViewableEventTypes()
        => KnownEventTypes.Where(CanViewEventType).ToHashSet(StringComparer.Ordinal);

    /// <summary>As above, but scoped to one staff member — the only safe form for data queries.</summary>
    public HashSet<string> ViewableEventTypesFor(Guid staffUserId)
        => KnownEventTypes.Where(t => CanViewEventTypeFor(t, staffUserId)).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The wire projection of this principal's effective authorities.
    ///
    /// Lives here, not in <see cref="IAccessPolicy"/>, because it is also produced in bulk: the
    /// org-change fan-out builds every principal in the company from three batch queries and must
    /// project them exactly as a singly-loaded principal would (B5).
    /// </summary>
    public EffectiveAuthoritiesDto ToEffectiveAuthorities()
        => IsAdmin
            ? new EffectiveAuthoritiesDto
            {
                UserId = UserId,
                IsAdmin = true,
                IsPoliceman = false,
                AuthorityVersion = long.MaxValue,
                Packages = AuthorityPackageIds.All.ToList()
            }
            : new EffectiveAuthoritiesDto
            {
                UserId = UserId,
                IsAdmin = false,
                IsPoliceman = IsPoliceman,
                AuthorityVersion = AuthorityVersion,
                Packages = Packages.OrderBy(p => p).ToList()
            };

    private static HashSet<string> BuildEffective(
        UserRole role,
        bool isPoliceman,
        IReadOnlySet<string> granted,
        bool isLeader)
    {
        if (role == UserRole.Admin)
        {
            return [.. AuthorityPackageIds.All];
        }

        var set = isPoliceman
            ? new HashSet<string>(granted, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        if (isLeader)
        {
            foreach (var package in InherentLeaderPackages)
            {
                set.Add(package);
            }
        }

        return set;
    }
}
