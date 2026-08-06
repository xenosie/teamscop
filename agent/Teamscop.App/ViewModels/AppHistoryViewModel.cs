using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Sync;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed partial class AppHistoryRowViewModel : ObservableObject
{
    public Guid EventId { get; init; }
    public string EventType { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string TimeLabel { get; init; } = "";
}

public sealed partial class AppHistoryViewModel : ObservableObject
{
    private readonly LocalAgentStore _store;
    private string _apiBaseUrl;
    private Guid? _loadedForStaff;
    private DateTimeOffset? _loadedFromUtc;
    private DateTimeOffset? _loadedToUtc;
    private int _loadGeneration;

    public AppHistoryViewModel(string? apiBaseUrl = null)
    {
        _store = AppSessionStore.ForActiveSession();
        _apiBaseUrl = ResolveApiBase(apiBaseUrl);
    }

    public ObservableCollection<AppHistoryRowViewModel> Items { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _emptyMessage;

    public bool HasItems => Items.Count > 0;
    public bool ShowEmpty => !IsLoading && string.IsNullOrWhiteSpace(ErrorMessage) && !HasItems;
    public bool ShowError => !IsLoading && !string.IsNullOrWhiteSpace(ErrorMessage);

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowError));
    }

    public void Reset()
    {
        Interlocked.Increment(ref _loadGeneration);
        _loadedForStaff = null;
        _loadedFromUtc = null;
        _loadedToUtc = null;
        Items.Clear();
        ErrorMessage = null;
        EmptyMessage = null;
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    public async Task LoadAsync(
        Guid staffUserId,
        bool force = false,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null)
    {
        if (!force
            && _loadedForStaff == staffUserId
            && _loadedFromUtc == fromUtc
            && _loadedToUtc == toUtc
            && (HasItems || ShowEmpty || ShowError))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _loadGeneration);
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Items.Clear();
                ErrorMessage = "Sign in required.";
                EmptyMessage = null;
                IsLoading = false;
                OnPropertyChanged(nameof(HasItems));
            });
            return;
        }

        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
            EmptyMessage = null;
        });

        try
        {
            var clock = new BusinessClock();
            try
            {
                using var bizApi = new BusinessTimeApiClient(_apiBaseUrl);
                var cfg = await bizApi.GetMineAsync(state.AccessToken).ConfigureAwait(false);
                clock.Apply(cfg);
            }
            catch
            {
                // Fall back to payload / UTC formatting.
            }

            using var api = new TrackingApiClient(_apiBaseUrl);
            // Per-type fetch covers all PHASE9 lifecycle events even under dense tracking traffic.
            var events = await api.QueryAppHistoryAsync(
                    state.AccessToken, staffUserId, take: 300, from: fromUtc, to: toUtc)
                .ConfigureAwait(false);
            var rows = events.Select(e => ToRow(e, clock)).ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Items.Clear();
                foreach (var row in rows)
                {
                    Items.Add(row);
                }

                _loadedForStaff = staffUserId;
                _loadedFromUtc = fromUtc;
                _loadedToUtc = toUtc;
                EmptyMessage = rows.Count == 0
                    ? (fromUtc is not null || toUtc is not null
                        ? "No lifecycle events in this period."
                        : "No lifecycle events yet.")
                    : null;
                ErrorMessage = null;
                IsLoading = false;
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(ShowEmpty));
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Items.Clear();
                ErrorMessage = FormatError(ex);
                EmptyMessage = null;
                IsLoading = false;
                _loadedForStaff = staffUserId;
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(ShowEmpty));
            });
        }
    }

    private static AppHistoryRowViewModel ToRow(TrackingEventItem e, BusinessClock companyClock)
    {
        var (title, detail) = Describe(e);
        return new AppHistoryRowViewModel
        {
            EventId = e.Id,
            EventType = e.EventType,
            OccurredAt = e.OccurredAt,
            Title = title,
            Detail = detail,
            TimeLabel = BusinessTimeDisplay.FormatEventTime(e, companyClock)
        };
    }

    private static (string Title, string Detail) Describe(TrackingEventItem e)
    {
        try
        {
            using var doc = OpenPayload(e.PayloadJson);
            var root = doc.RootElement;
            return e.EventType switch
            {
                AgentEventTypes.Registration => (
                    "Registration",
                    FormatRegistration(root)),
                AgentEventTypes.PowerOff => (
                    "Power off",
                    FormatPowerOff(root)),
                AgentEventTypes.UsbEvent => (
                    "USB",
                    FormatUsb(root)),
                AgentEventTypes.Uninstall => (
                    "Uninstall",
                    FormatUninstall(root)),
                AgentEventTypes.AppBroken => (
                    "App broken",
                    FormatAppBroken(root)),
                _ => (e.EventType, "Event recorded")
            };
        }
        catch (JsonException)
        {
            return (e.EventType, "Event recorded");
        }
    }

    private static JsonDocument OpenPayload(string? payloadJson)
    {
        var raw = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;
        using var outer = JsonDocument.Parse(raw);
        var root = outer.RootElement;
        if (root.TryGetProperty("payloadBase64", out var pb)
            && pb.ValueKind == JsonValueKind.String)
        {
            var b64 = pb.GetString();
            if (!string.IsNullOrWhiteSpace(b64))
            {
                try
                {
                    return JsonDocument.Parse(Convert.FromBase64String(b64));
                }
                catch (Exception ex) when (ex is FormatException or JsonException)
                {
                    // Fall through to outer document.
                }
            }
        }

        // Re-parse so caller owns the document (outer is disposed).
        return JsonDocument.Parse(raw);
    }

    private static string FormatRegistration(JsonElement root)
    {
        var username = ReadString(root, "username", "Username");
        var prefix = ReadString(root, "deviceKeyPrefix", "DeviceKeyPrefix");
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(prefix))
        {
            return $"Joined as {username} · device {prefix}…";
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            return $"Joined as {username}";
        }

        return "Staff account created";
    }

    private static string FormatPowerOff(JsonElement root)
    {
        var reason = ReadString(root, "reason", "Reason");
        return reason switch
        {
            "service_stop" => "Agent service stopped",
            "shutdown" => "Machine shutdown",
            { Length: > 0 } other => other.Replace('_', ' '),
            _ => "Session ended"
        };
    }

    private static string FormatUsb(JsonElement root)
    {
        var action = ReadString(root, "action", "Action")?.Replace('_', ' ') ?? "USB activity";
        var name = ReadString(root, "friendlyName", "FriendlyName");
        var drive = ReadString(root, "driveLetter", "DriveLetter");
        var parts = new List<string> { action };
        if (!string.IsNullOrWhiteSpace(name))
        {
            parts.Add(name);
        }

        if (!string.IsNullOrWhiteSpace(drive))
        {
            parts.Add(drive.EndsWith(':') ? drive : drive + ":");
        }

        var error = ReadString(root, "error", "Error");
        if (!string.IsNullOrWhiteSpace(error))
        {
            parts.Add(error);
        }

        return string.Join(" · ", parts);
    }

    private static string FormatUninstall(JsonElement root)
    {
        var ticket = ReadString(root, "ticketId", "TicketId");
        return string.IsNullOrWhiteSpace(ticket)
            ? "Uninstall ticket consumed"
            : $"Uninstall ticket consumed · {ticket}";
    }

    private static string FormatAppBroken(JsonElement root)
    {
        var parts = new List<string>();
        if (root.TryGetProperty("missing", out var missing) && missing.ValueKind == JsonValueKind.Array)
        {
            var names = missing.EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(3)
                .ToList();
            if (names.Count > 0)
            {
                parts.Add("Missing: " + string.Join(", ", names));
            }
        }

        if (root.TryGetProperty("altered", out var altered) && altered.ValueKind == JsonValueKind.Array)
        {
            var names = altered.EnumerateArray()
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(3)
                .ToList();
            if (names.Count > 0)
            {
                parts.Add("Altered: " + string.Join(", ", names));
            }
        }

        var rootPath = ReadString(root, "installRoot", "InstallRoot");
        if (!string.IsNullOrWhiteSpace(rootPath) && parts.Count == 0)
        {
            parts.Add(rootPath);
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "Install integrity incident";
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }
        }

        return null;
    }

    private static string FormatError(Exception ex)
    {
        while (ex is AggregateException { InnerException: { } inner })
        {
            ex = inner;
        }

        if (ex is ApiClientException api)
        {
            var msg = api.Message;
            const string prefix = "Tracking API: ";
            return msg.StartsWith(prefix, StringComparison.Ordinal) ? msg[prefix.Length..] : msg;
        }

        return ex is HttpRequestException
            ? "Could not reach the server."
            : (string.IsNullOrWhiteSpace(ex.Message) ? "Failed to load app history." : ex.Message);
    }

    private static string ResolveApiBase(string? apiBaseUrl)
        => string.IsNullOrWhiteSpace(apiBaseUrl)
            ? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE") ?? "https://teamscop.com"
            : apiBaseUrl.TrimEnd('/');
}
