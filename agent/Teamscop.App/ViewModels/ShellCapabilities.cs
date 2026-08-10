using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.ViewModels;

/// <summary>
/// What the caller may see, computed once from their effective authorities and org placement.
/// One window adapts to this record (§4.7); there is no shell "kind" — leader + policeman is
/// simply the union of two booleans.
/// </summary>
public sealed record ShellCapabilities(
    bool IsAdmin,
    bool IsTeamLeader,
    bool IsPoliceman,
    bool CanViewScreenshot,
    bool CanViewBrowsing,
    bool CanViewTimeTrack,
    bool CanViewAppHistory,
    bool CanManageTeams,
    bool CanIssueCodes,
    bool CanEditStaffTracking,
    bool CanEditCompanySettings)
{
    /// <summary>A signed-in user whose authorities have not loaded yet — sees nothing.</summary>
    public static readonly ShellCapabilities None = new(
        IsAdmin: false,
        IsTeamLeader: false,
        IsPoliceman: false,
        CanViewScreenshot: false,
        CanViewBrowsing: false,
        CanViewTimeTrack: false,
        CanViewAppHistory: false,
        CanManageTeams: false,
        CanIssueCodes: false,
        CanEditStaffTracking: false,
        CanEditCompanySettings: false);

    public bool CanSeeAnyStaffData => CanViewScreenshot || CanViewBrowsing || CanViewTimeTrack;

    /// <summary>False for plain staff: they get the sticker and nothing else (§4.4).</summary>
    public bool HasWorkspace => CanSeeAnyStaffData || CanManageTeams || CanIssueCodes;

    public string RoleLabel => this switch
    {
        { IsAdmin: true } => "Admin",
        { IsTeamLeader: true, IsPoliceman: true } => "Team leader · Policeman",
        { IsTeamLeader: true } => "Team leader",
        { IsPoliceman: true } => "Policeman",
        _ => "Staff"
    };

    public static ShellCapabilities From(EffectiveAuthoritiesDto? authorities, MyOrgPlacementDto? placement)
    {
        if (authorities is null)
        {
            return None;
        }

        var isAdmin = authorities.IsAdmin;
        var packages = new HashSet<string>(authorities.Packages, StringComparer.Ordinal);

        bool Has(string package) => isAdmin || packages.Contains(package);

        // Team leaders receive the three view packages from the server, scoped to their team,
        // so package membership is the whole rule here — no role special-casing.
        var canIssueCodes = Has(AuthorityPackageIds.UsbApproval) || Has(AuthorityPackageIds.UninstallApproval);

        return new ShellCapabilities(
            IsAdmin: isAdmin,
            IsTeamLeader: placement?.IsTeamLeader ?? false,
            IsPoliceman: authorities.IsPoliceman,
            CanViewScreenshot: Has(AuthorityPackageIds.ViewScreenshot),
            CanViewBrowsing: Has(AuthorityPackageIds.ViewBrowserHistory),
            CanViewTimeTrack: Has(AuthorityPackageIds.ViewTimeTrack),
            // Keyed on uninstall_approval alone, matching the server. ViewerContext gates the
            // registration / power_off / uninstall event types on that one package, so deriving this
            // from "can issue any code" showed the App History screen to a policeman holding only
            // usb_approval — who then got 403 from every request the screen made.
            CanViewAppHistory: Has(AuthorityPackageIds.UninstallApproval),
            CanManageTeams: Has(AuthorityPackageIds.TeamManagement),
            CanIssueCodes: canIssueCodes,
            CanEditStaffTracking: isAdmin,
            CanEditCompanySettings: isAdmin);
    }
}
