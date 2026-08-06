using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.App.Services;

/// <summary>
/// Decides whether this machine already has a registered Admin or Staff session
/// that can open the dashboard (admins, team leaders, policemen).
/// </summary>
public static class AdminSessionGate
{
    public static bool TryLoadRegisteredAdmin(out LocalAgentState state, out string statePath)
        => TryLoadRegisteredSession(out state, out statePath);

    public static bool TryLoadRegisteredSession(out LocalAgentState state, out string statePath)
    {
        if (TryLoadRole(AgentRole.Admin, requireAdminRole: true, out state, out statePath))
        {
            return true;
        }

        return TryLoadRole(AgentRole.Staff, requireAdminRole: false, out state, out statePath);
    }

    private static bool TryLoadRole(
        AgentRole role,
        bool requireAdminRole,
        out LocalAgentState state,
        out string statePath)
    {
        var store = new LocalAgentStore(role);
        statePath = store.StatePath;
        state = store.Load();

        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            return false;
        }

        if (requireAdminRole)
        {
            if (!AppSessionStore.IsAdminRole(state.Role))
            {
                return false;
            }
        }
        else if (AppSessionStore.IsAdminRole(state.Role))
        {
            return false;
        }

        // Bound to this machine's device key — fail closed if key cannot be read or mismatches.
        try
        {
            var deviceKey = new DeviceKeyProvider().GetDeviceKey();
            if (string.IsNullOrWhiteSpace(deviceKey))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(state.DeviceKey)
                && !string.Equals(state.DeviceKey, deviceKey, StringComparison.Ordinal))
            {
                return false;
            }

            // Persist binding when older state lacked DeviceKey but token is valid for this machine.
            if (string.IsNullOrWhiteSpace(state.DeviceKey))
            {
                state.DeviceKey = deviceKey;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }
}
