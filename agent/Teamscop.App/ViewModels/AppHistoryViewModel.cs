using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private int _loadGeneration;

    public AppHistoryViewModel(string? apiBaseUrl = null)
    {
        _store = new LocalAgentStore(AgentRole.Admin);
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
        _loadedForStaff = null;
        Items.Clear();
        ErrorMessage = null;
        EmptyMessage = null;
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    public async Task LoadAsync(Guid staffUserId, bool force = false)
    {
        if (!force && _loadedForStaff == staffUserId && (HasItems || ShowEmpty || ShowError))
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
            using var api = new TrackingApiClient(_apiBaseUrl);
            var events = await api.QueryAppHistoryAsync(state.AccessToken, staffUserId, take: 200)
                .ConfigureAwait(false);
            var rows = events.Select(ToRow).ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _loadGeneration) return;
                Items.Clear();
                foreach (var row in rows)
                {
                    Items.Add(row);
                }

                _loadedForStaff = staffUserId;
                EmptyMessage = rows.Count == 0 ? "No lifecycle events yet." : null;
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

    private static AppHistoryRowViewModel ToRow(TrackingEventItem e)
    {
        var (title, detail) = Describe(e);
        return new AppHistoryRowViewModel
        {
            EventId = e.Id,
            EventType = e.EventType,
            OccurredAt = e.OccurredAt,
            Title = title,
            Detail = detail,
            TimeLabel = e.OccurredAt.ToLocalTime().ToString("g")
        };
    }

    private static (string Title, string Detail) Describe(TrackingEventItem e)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(e.PayloadJson) ? "{}" : e.PayloadJson);
            var root = doc.RootElement;
            return e.EventType switch
            {
                AgentEventTypes.Registration => (
                    "Registration",
                    root.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String
                        ? $"Joined as {u.GetString()}"
                        : "Staff account created"),
                AgentEventTypes.PowerOff => (
                    "Power off",
                    root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                        ? r.GetString() switch
                        {
                            "service_stop" => "Agent service stopped",
                            "shutdown" => "Machine shutdown",
                            var other => other ?? "Session ended"
                        }
                        : "Session ended"),
                AgentEventTypes.UsbEvent => (
                    "USB",
                    root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
                        ? a.GetString()!.Replace('_', ' ')
                        : "USB activity"),
                AgentEventTypes.Uninstall => (
                    "Uninstall",
                    "Uninstall ticket consumed"),
                AgentEventTypes.AppBroken => (
                    "App broken",
                    FormatAppBroken(root)),
                _ => (e.EventType, Truncate(e.PayloadJson, 120))
            };
        }
        catch (JsonException)
        {
            return (e.EventType, "Event recorded");
        }
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

        return parts.Count > 0 ? string.Join(" · ", parts) : "Install integrity incident";
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

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    private static string ResolveApiBase(string? apiBaseUrl)
        => string.IsNullOrWhiteSpace(apiBaseUrl)
            ? Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE") ?? "https://teamscop.com"
            : apiBaseUrl.TrimEnd('/');
}
