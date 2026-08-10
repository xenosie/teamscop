using Teamscop.App.ViewModels;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Services;

/// <summary>
/// The single owner of who the caller is and what they hold. Authorities arrive from
/// /api/police/me, placement from /api/org/me, and both can change mid-session over SignalR —
/// so this raises <see cref="Changed"/> and the shell re-reads its capabilities instead of
/// requiring a restart.
/// </summary>
public sealed class AuthorityState
{
    private readonly object _gate = new();
    private EffectiveAuthoritiesDto? _authorities;
    private MyOrgPlacementDto? _placement;
    private ShellCapabilities _capabilities = ShellCapabilities.None;

    /// <summary>Raised on the thread that called <see cref="Apply"/> — callers marshal to the UI.</summary>
    public event Action? Changed;

    public ShellCapabilities Capabilities
    {
        get { lock (_gate) return _capabilities; }
    }

    public EffectiveAuthoritiesDto? Authorities
    {
        get { lock (_gate) return _authorities; }
    }

    public MyOrgPlacementDto? Placement
    {
        get { lock (_gate) return _placement; }
    }

    public bool IsAdmin => Capabilities.IsAdmin;

    public string? TeamName
    {
        get { lock (_gate) return _placement?.TeamName; }
    }

    public bool Has(string packageId)
    {
        lock (_gate)
        {
            return _authorities is { } auth
                   && (auth.IsAdmin || auth.Packages.Contains(packageId, StringComparer.Ordinal));
        }
    }

    /// <summary>Applies either half; the other keeps its last known value. No-ops when nothing moved.</summary>
    public void Apply(EffectiveAuthoritiesDto? authorities = null, MyOrgPlacementDto? placement = null)
    {
        if (authorities is null && placement is null)
        {
            return;
        }

        ShellCapabilities next;
        bool changed;
        lock (_gate)
        {
            _authorities = authorities ?? _authorities;
            _placement = placement ?? _placement;
            next = ShellCapabilities.From(_authorities, _placement);
            changed = next != _capabilities;
            _capabilities = next;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    public void Clear()
    {
        bool changed;
        lock (_gate)
        {
            changed = _capabilities != ShellCapabilities.None;
            _authorities = null;
            _placement = null;
            _capabilities = ShellCapabilities.None;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }
}
