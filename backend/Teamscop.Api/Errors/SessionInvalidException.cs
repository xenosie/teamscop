namespace Teamscop.Api.Errors;

/// <summary>
/// The bearer token verified, but the account behind it no longer resolves — 401, not 403.
///
/// The distinction is load-bearing for the desktop client. 403 means "you are someone, but not
/// allowed here", so the shell keeps the session and falls back to cached data. 401 means "you are
/// nobody", which is the only signal that sends it back to the login screen (§3.2). Returning 403
/// for a dead session left the app showing "Working from a cached session" and retrying forever,
/// with no way for the user to sign in again.
/// </summary>
public sealed class SessionInvalidException : Exception
{
    public SessionInvalidException()
        : base("Session is no longer valid. Sign in again.")
    {
    }

    public SessionInvalidException(string message)
        : base(message)
    {
    }
}
