namespace Teamscop.Api.Audit;

/// <summary>The closed vocabulary of security-relevant operations. Grep-friendly by design.</summary>
public static class AuditActions
{
    public const string CompanyCreated = "company.created";
    public const string StaffEnrolled = "staff.enrolled";
    public const string PasswordChanged = "password.changed";
    public const string CompanyTimeZoneChanged = "company.timezone.changed";

    public const string PoliceGranted = "police.granted";
    public const string PoliceRevoked = "police.revoked";

    public const string TeamCreated = "team.created";
    public const string TeamUpdated = "team.updated";
    public const string TeamDeleted = "team.deleted";
    public const string TeamMembersReplaced = "team.members.replaced";
    public const string TeamMemberAdded = "team.member.added";
    public const string TeamMemberRemoved = "team.member.removed";

    public const string TrackingConfigChanged = "tracking.config.changed";

    public const string TotpEnrolled = "totp.enrolled";
    public const string TotpCodeIssued = "totp.code.issued";

    public const string UsbApprovalGranted = "approval.usb.granted";
    public const string UsbApprovalDenied = "approval.usb.denied";
    public const string UsbApprovalConsumed = "approval.usb.consumed";
    public const string UninstallApprovalGranted = "approval.uninstall.granted";
    public const string UninstallApprovalDenied = "approval.uninstall.denied";
    public const string UninstallApprovalConsumed = "approval.uninstall.consumed";
}
