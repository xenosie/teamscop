namespace Teamscop.App.Services;

public enum RoleShellKind
{
    Admin,
    Leader,
    Police,
    Officer,
    /// <summary>Plain staff — floating 24h timetrack sticker only (no workspace window).</summary>
    StaffSticker,
    NoAccess
}
