namespace Teamscop.Engine.Lifecycle;

/// <summary>
/// Individually packaged authorities. Admin implicitly has all.
/// Policemen receive any subset; powers apply company-wide.
/// </summary>
public static class AuthorityPackageIds
{
    public const string ViewScreenshot = "view_screenshot";
    public const string ViewTimeTrack = "view_timetrack";
    public const string ViewBrowserHistory = "view_browser_history";
    public const string UsbApproval = "usb_approval";
    public const string UninstallApproval = "uninstall_approval";
    public const string TeamManagement = "team_management";

    public static readonly IReadOnlyList<string> All =
    [
        ViewScreenshot,
        ViewTimeTrack,
        ViewBrowserHistory,
        UsbApproval,
        UninstallApproval,
        TeamManagement
    ];

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [ViewScreenshot] = "Screenshot view",
        [ViewTimeTrack] = "Timetrack view",
        [ViewBrowserHistory] = "Browsing history view",
        [UsbApproval] = "USB approval (generate TOTP)",
        [UninstallApproval] = "Uninstall approval (generate TOTP)",
        [TeamManagement] = "Team management"
    };

    public static bool IsKnown(string packageId)
        => All.Contains(packageId, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string packageId)
    {
        var hit = All.FirstOrDefault(p => p.Equals(packageId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (hit is null)
        {
            throw new ArgumentException($"Unknown authority package: {packageId}");
        }

        return hit;
    }
}

public sealed class AuthorityPackageInfo
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class EffectiveAuthoritiesDto
{
    public Guid UserId { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsPoliceman { get; set; }
    public long AuthorityVersion { get; set; }
    public List<string> Packages { get; set; } = [];
}

public sealed class PolicemanDto
{
    public Guid StaffUserId { get; set; }
    public string Username { get; set; } = "";
    public bool IsPoliceman { get; set; }
    public List<string> Packages { get; set; } = [];
    public DateTimeOffset? UpdatedAt { get; set; }
}
