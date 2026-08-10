using Teamscop.App.Composition;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Services;

/// <summary>
/// The app's single session: which local store is active (admin under LocalAppData, staff under
/// ProgramData), the cached state, and — exactly once in the whole app — how the API base URL is
/// resolved. Replaces AppSessionStore and AdminSessionGate.
/// </summary>
public sealed class SessionStore
{
    public const string DefaultApiBaseUrl = "https://teamscop.com";

    /// <summary>
    /// The path prefix avatars were stored under before B12 moved media behind an authorized route.
    /// Rows written by an older server still hold it, and the old alias is gone: the dead path is
    /// answered by nginx's catch-all with 200 text/plain, so it never even fails cleanly.
    /// </summary>
    private const string LegacyAvatarPrefix = "/media/avatars/";

    private const string CurrentAvatarPrefix = "/api/media/avatars/";

    private readonly IDeviceKeyProvider _deviceKeys;
    private readonly UiLog _log;

    /// <summary>Test seam: redirects both role stores to a scratch directory. Null in production.</summary>
    private readonly string? _storeOverrideDirectory;

    private readonly object _deviceKeyGate = new();
    private volatile string? _deviceKey;
    private readonly object _gate = new();
    private LocalAgentStore _store;
    private LocalAgentState _state;
    private AgentRole _role;
    private bool? _registered;

    public SessionStore(IDeviceKeyProvider deviceKeys, UiLog log, string? storeOverrideDirectory = null)
    {
        _deviceKeys = deviceKeys;
        _log = log;
        _storeOverrideDirectory = storeOverrideDirectory;
        _role = DetectInitialRole(storeOverrideDirectory);
        _store = OpenStore(_role);
        _state = _store.Load();
    }

    /// <summary>Raised when the server rejects the stored token — the shell returns to sign-in.</summary>
    public event Action? Invalidated;

    public AgentRole ActiveRole
    {
        get { lock (_gate) return _role; }
    }

    /// <summary>
    /// Staff-role session: the machine is monitored, so the sticker belongs on screen (§4.6).
    ///
    /// Defect 8 — the stored account role is checked as well as the active store. An admin token
    /// that ever lands in the staff store (a machine enrolled as staff and later re-registered as
    /// the admin console) would otherwise put a monitoring sticker on the owner's own screen and
    /// tell him he is being watched. "Admin" is decisive wherever the two disagree.
    /// </summary>
    public bool IsStaffRole => ActiveRole == AgentRole.Staff && !IsAdminRole(State.Role);

    /// <summary>Defect 8 — this session belongs to a company admin, who is never a monitored subject.</summary>
    public bool IsAdminSession => ActiveRole == AgentRole.Admin || IsAdminRole(State.Role);

    /// <summary>Last known state. Cheap — no disk read. Call <see cref="Reload"/> to re-read.</summary>
    public LocalAgentState State
    {
        get { lock (_gate) return _state; }
    }

    public string? AccessToken
    {
        get { lock (_gate) return _state.AccessToken; }
    }

    public bool HasToken => !string.IsNullOrWhiteSpace(AccessToken);

    /// <summary>
    /// Does this machine hold a session it may actually open the app with — a stored token whose
    /// device key still matches this hardware? Resolved once and cached, because start-up, the
    /// sticker and the shell all ask and must get the same answer.
    /// </summary>
    public bool IsRegistered
    {
        get
        {
            lock (_gate)
            {
                if (_registered is { } cached)
                {
                    return cached;
                }
            }

            var registered = TryLoadRegisteredSession(out _);
            lock (_gate)
            {
                _registered = registered;
            }

            return registered;
        }
    }

    public string StatePath
    {
        get { lock (_gate) return _store.StatePath; }
    }

    /// <summary>
    /// The one place the API base URL is decided. Stored value wins; TEAMSCOP_API_BASE covers a
    /// machine that has not enrolled yet; the SaaS host is the last resort.
    /// </summary>
    public string ApiBaseUrl
    {
        get
        {
            lock (_gate)
            {
                return Resolve(_state.ApiBaseUrl);
            }
        }
    }

    /// <summary>
    /// This machine's hardware identity, or empty when it cannot be derived. Deliberately broad:
    /// device identity gates the session, so any failure has to fail closed rather than crash
    /// start-up (the provider shells out to SMBIOS/NIC/disk queries that can fail many ways).
    /// </summary>
    /// <remarks>
    /// Cached after the first derivation. The provider spawns several SMBIOS/NIC/disk queries
    /// (four PowerShell CIM calls on Windows), which is far too slow to sit on the UI thread —
    /// see <see cref="DeviceKeyAsync"/> for the start-up path.
    /// </remarks>
    public string DeviceKey
    {
        get
        {
            if (_deviceKey is not null)
            {
                return _deviceKey;
            }

            lock (_deviceKeyGate)
            {
                if (_deviceKey is not null)
                {
                    return _deviceKey;
                }

                try
                {
                    _deviceKey = _deviceKeys.GetDeviceKey();
                }
                catch (Exception ex)
                {
                    _log.Warn("Could not derive this machine's device key", ex);
                    _deviceKey = string.Empty;
                }

                return _deviceKey;
            }
        }
    }

    /// <summary>
    /// The device key off the calling thread. Anything constructed during start-up must use this:
    /// deriving it synchronously froze the UI thread before the first frame was drawn.
    /// </summary>
    public Task<string> DeviceKeyAsync() =>
        _deviceKey is not null ? Task.FromResult(_deviceKey) : Task.Run(() => DeviceKey);

    public LocalAgentState Reload()
    {
        lock (_gate)
        {
            _state = _store.Load();
            return _state;
        }
    }

    public void Save(LocalAgentState state)
    {
        lock (_gate)
        {
            _store.Save(state);
            _state = state;
            _registered = !string.IsNullOrWhiteSpace(state.AccessToken);
        }
    }

    /// <summary>Persist into a specific role's store and make it the active session.</summary>
    public void SaveFor(AgentRole role, LocalAgentState state)
    {
        lock (_gate)
        {
            _role = role;
            _store = OpenStore(role);
            _store.Save(state);
            _state = state;
            // Freshly proven against the server for this device key.
            _registered = !string.IsNullOrWhiteSpace(state.AccessToken);
        }

        // Defect 8 — the one moment this machine's role is actually known. Every enrolment and
        // every sign-in funnels through here, so the agent's capture gate gets its signal without
        // any screen having to remember to send it.
        InstalledRoleWriter.TryStamp(InstalledRoleWriter.RoleFor(role, state.Role), _log);
    }

    public void SetActiveRole(string? role) => SetActiveRole(ToAgentRole(role));

    public void SetActiveRole(AgentRole role)
    {
        lock (_gate)
        {
            if (_role == role)
            {
                return;
            }

            _role = role;
            _store = OpenStore(role);
            _state = _store.Load();
        }
    }

    public void Invalidate() => Invalidated?.Invoke();

    /// <summary>
    /// Does this machine hold a session that can open the app? Admin store first, then staff.
    /// Fails closed when the stored device key does not match this hardware.
    /// </summary>
    public bool TryLoadRegisteredSession(out LocalAgentState state)
    {
        if (TryLoadRole(AgentRole.Admin, requireAdminRole: true, out state)
            || TryLoadRole(AgentRole.Staff, requireAdminRole: false, out state))
        {
            SetActiveRole(ToAgentRole(state.Role));
            lock (_gate)
            {
                _state = state;
            }

            return true;
        }

        return false;
    }

    /// <summary>Turns a server-relative "/api/media/…" path into an absolute URL on the active host.</summary>
    public string? ToAbsoluteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var normalized = NormalizeMediaPath(url);

        // "/api/media/..." is NOT an http URL — UriKind.Absolute would wrongly make it file://
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var abs)
            && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
        {
            return abs.ToString();
        }

        return $"{ApiBaseUrl}/{normalized.TrimStart('/')}";
    }

    /// <summary>
    /// Rewrites the pre-B12 avatar prefix onto the authorized route. The server-side data repair
    /// fixes the rows it can reach, but a client that talks to an older server — or reads a row
    /// written before the repair ran — must still render the face rather than a placeholder.
    /// AvatarAccess resolves owners by file name, so the corrected prefix resolves the same file.
    /// Anchored on the prefix, so a path already on /api/media/avatars/ can never be doubled.
    /// </summary>
    public static string NormalizeMediaPath(string url)
        => url.StartsWith(LegacyAvatarPrefix, StringComparison.Ordinal)
            ? CurrentAvatarPrefix + url[LegacyAvatarPrefix.Length..]
            : url;

    public static bool IsAdminRole(string? role)
        => string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase);

    public static AgentRole ToAgentRole(string? role)
        => IsAdminRole(role) ? AgentRole.Admin : AgentRole.Staff;

    private static string Resolve(string? apiBaseUrl)
        => string.IsNullOrWhiteSpace(apiBaseUrl)
            ? (Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE")?.TrimEnd('/') ?? DefaultApiBaseUrl)
            : apiBaseUrl.TrimEnd('/');

    private static AgentRole DetectInitialRole(string? overrideDirectory)
    {
        var admin = OpenStore(AgentRole.Admin, overrideDirectory);
        return string.IsNullOrWhiteSpace(admin.Load().AccessToken) ? AgentRole.Staff : AgentRole.Admin;
    }

    private LocalAgentStore OpenStore(AgentRole role) => OpenStore(role, _storeOverrideDirectory);

    /// <summary>Both role stores stay distinct under an override, so admin/staff never collide.</summary>
    private static LocalAgentStore OpenStore(AgentRole role, string? overrideDirectory)
        => new(role, overrideDirectory is null
            ? null
            : Path.Combine(overrideDirectory, role == AgentRole.Admin ? "Admin" : "Staff"));

    private bool TryLoadRole(AgentRole role, bool requireAdminRole, out LocalAgentState state)
    {
        state = OpenStore(role).Load();

        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            return false;
        }

        if (requireAdminRole)
        {
            if (!IsAdminRole(state.Role))
            {
                return false;
            }
        }
        else if (IsAdminRole(state.Role))
        {
            return false;
        }

        // Bound to this machine's device key — fail closed if the key cannot be read or mismatches.
        var deviceKey = DeviceKey;
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(state.DeviceKey)
            && !string.Equals(state.DeviceKey, deviceKey, StringComparison.Ordinal))
        {
            return false;
        }

        // Older state predates the binding but the token is valid for this machine.
        if (string.IsNullOrWhiteSpace(state.DeviceKey))
        {
            state.DeviceKey = deviceKey;
        }

        return true;
    }
}
