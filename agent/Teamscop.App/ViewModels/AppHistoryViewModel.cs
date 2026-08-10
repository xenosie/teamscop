using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Teamscop.App.Composition;
using Teamscop.App.Services;
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
    /// <summary>A long-lived machine has hundreds of lifecycle rows; show a page (§15.1).</summary>
    private const int PageSize = 25;

    private readonly TeamscopApi _api;
    private readonly SessionStore _session;
    private readonly CompanyClock _clock;
    private readonly UiLog _log;
    private readonly PageWindow<AppHistoryRowViewModel> _page;
    private Guid? _loadedForStaff;
    private DateTimeOffset? _loadedFromUtc;
    private DateTimeOffset? _loadedToUtc;
    private int _loadGeneration;

    public AppHistoryViewModel(AppServices services)
    {
        _api = services.Api;
        _session = services.Session;
        _clock = services.Clock;
        _log = services.Log;
        _page = new PageWindow<AppHistoryRowViewModel>(Items, PageSize);
    }

    public ObservableCollection<AppHistoryRowViewModel> Items { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _emptyMessage;

    public bool HasItems => Items.Count > 0;
    public bool ShowEmpty => !IsLoading && string.IsNullOrWhiteSpace(ErrorMessage) && !HasItems;
    public bool ShowError => !IsLoading && !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsPaged => _page.IsPaged;
    public bool HasMore => _page.HasMore;
    public string PageLabel => _page.Label;

    [RelayCommand]
    private void ShowMore()
    {
        _page.More();
        RaisePaging();
    }

    [RelayCommand]
    private void ShowAll()
    {
        _page.All();
        RaisePaging();
    }

    private void RaisePaging()
    {
        OnPropertyChanged(nameof(IsPaged));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(PageLabel));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmpty));
    }

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
        _page.Reset([]);
        ErrorMessage = null;
        EmptyMessage = null;
        RaisePaging();
    }

    public async Task LoadAsync(
        Guid staffUserId,
        bool force = false,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        CancellationToken ct = default)
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
        if (!_session.HasToken)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                _page.Reset([]);
                ErrorMessage = "Sign in required.";
                EmptyMessage = null;
                IsLoading = false;
                RaisePaging();
            });
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
            EmptyMessage = null;
        });

        try
        {
            // Per-type fetch covers all PHASE9 lifecycle events even under dense tracking traffic.
            var events = await _api.QueryAppHistoryAsync(
                    staffUserId, take: 300, from: fromUtc, to: toUtc, ct)
                .ConfigureAwait(false);
            var rows = events.Select(ToRow).ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                _page.Reset(rows);
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
                RaisePaging();
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled by a newer selection or by leaving the route. Hand the section back rather
            // than leaving the spinner up: a newer load owns the flag once the generation moves.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            _log.Warn($"App history for {staffUserId:D} could not be loaded", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                _page.Reset([]);
                ErrorMessage = ApiError.Describe(ex, "Failed to load app history.");
                EmptyMessage = null;
                IsLoading = false;
                _loadedForStaff = staffUserId;
                RaisePaging();
            });
        }
    }

    private AppHistoryRowViewModel ToRow(TrackingEventItem e)
    {
        var (title, detail) = Describe(e);
        return new AppHistoryRowViewModel
        {
            EventId = e.Id,
            EventType = e.EventType,
            OccurredAt = e.OccurredAt,
            Title = title,
            Detail = detail,
            TimeLabel = _clock.FormatEventTime(e)
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
                AgentEventTypes.Install => (
                    "Installed",
                    FormatInstall(root)),
                AgentEventTypes.AppStatus => (
                    "Status",
                    FormatAppStatus(root)),
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

    private static string FormatInstall(JsonElement root)
    {
        var stamp = ReadString(root, "stamp", "Stamp");
        return string.IsNullOrWhiteSpace(stamp)
            ? "Product installed or upgraded"
            : $"Product installed · build {stamp}";
    }

    /// <summary>
    /// The four-state transitions, in plain words. "broken → components_missing:Teamscop.App.exe"
    /// is the row the owner reads when a machine was intentionally damaged, so the reason is shown
    /// verbatim rather than summarised away.
    /// </summary>
    private static string FormatAppStatus(JsonElement root)
    {
        var status = ReadString(root, "status", "Status") ?? "unknown";
        var previous = ReadString(root, "previous", "Previous");
        var reason = ReadString(root, "reason", "Reason");
        var label = status switch
        {
            "online" => "Online",
            "offline" => "Offline",
            "broken" => "BROKEN",
            "uninstalled" => "Uninstalled / not installed",
            _ => status
        };
        var arrow = string.IsNullOrWhiteSpace(previous) ? label : $"{previous} → {label}";
        return string.IsNullOrWhiteSpace(reason) ? arrow : $"{arrow} · {reason.Replace('_', ' ')}";
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

}
