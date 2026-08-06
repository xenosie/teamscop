using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Teamscop.App.Services;
using Teamscop.Engine.Auth;
using Teamscop.Engine.Lifecycle;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.ViewModels;

public sealed class TimeTrackStickerSegment
{
    public TimeTrackStickerSegment(string kind, double durationSeconds)
    {
        Kind = kind;
        DurationSeconds = Math.Max(0.001, durationSeconds);
        Brush = kind switch
        {
            "working" => WorkingFill,
            "rest" => RestFill,
            _ => Brushes.Transparent
        };
    }

    private static readonly IBrush WorkingFill = SolidColorBrush.Parse("#16A34A");
    private static readonly IBrush RestFill = SolidColorBrush.Parse("#DC2626");

    public string Kind { get; }
    public double DurationSeconds { get; }
    public IBrush Brush { get; }
}

/// <summary>
/// Plain-staff sticker: last-24h working/rest bar, no numbers or captions.
/// </summary>
public sealed partial class TimeTrackStickerViewModel : ObservableObject, IAsyncDisposable
{
    private readonly LocalAgentStore _store;
    private readonly string _apiBaseUrl;
    private readonly DispatcherTimer _timer;
    private Guid? _userId;
    private int _loadGeneration;
    private bool _disposed;

    public TimeTrackStickerViewModel(LocalAgentState state)
    {
        AppSessionStore.SetActive(state.Role);
        _store = AppSessionStore.Create(state.Role);
        _apiBaseUrl = ResolveApiBase(state.ApiBaseUrl);
        _userId = state.UserId;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    public ObservableCollection<TimeTrackStickerSegment> Segments { get; } = [];

    [ObservableProperty] private bool _hasTimeline;

    public async Task InitializeAsync()
    {
        await RefreshAsync().ConfigureAwait(true);
        if (!_disposed)
        {
            _timer.Start();
        }
    }

    public async Task RefreshAsync()
    {
        var gen = Interlocked.Increment(ref _loadGeneration);
        var state = _store.Load();
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            return;
        }

        try
        {
            if (_userId is null || _userId == Guid.Empty)
            {
                using var auth = new AuthApiClient(_apiBaseUrl);
                var me = await auth.MeAsync(state.AccessToken).ConfigureAwait(false);
                _userId = me.Id;
                state.UserId = me.Id;
                try { _store.Save(state); } catch { /* best-effort */ }
            }

            var to = DateTimeOffset.UtcNow;
            var from = to.AddHours(-24);
            using var api = new TrackingApiClient(_apiBaseUrl);
            var timeline = await api.QueryTimeTrackTimelineAsync(
                state.AccessToken, _userId.Value, from, to).ConfigureAwait(false);

            if (gen != _loadGeneration)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => ApplyTimeline(timeline));
        }
        catch
        {
            // Keep last painted bar; sticker stays silent on errors.
        }
    }

    private void ApplyTimeline(TimeTrackTimeline timeline)
    {
        Segments.Clear();
        if (timeline.Segments.Count == 0)
        {
            // Full-window gap so the strip still paints.
            Segments.Add(new TimeTrackStickerSegment("gap", 24 * 3600));
        }
        else
        {
            foreach (var seg in timeline.Segments)
            {
                var kind = NormalizeKind(seg.Kind);
                Segments.Add(new TimeTrackStickerSegment(kind, seg.DurationSeconds));
            }
        }

        HasTimeline = Segments.Count > 0;
    }

    private static string NormalizeKind(string? kind)
    {
        if (string.Equals(kind, "working", StringComparison.OrdinalIgnoreCase))
        {
            return "working";
        }

        if (string.Equals(kind, "rest", StringComparison.OrdinalIgnoreCase))
        {
            return "rest";
        }

        return "gap";
    }

    private static string ResolveApiBase(string? apiBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            return apiBaseUrl.TrimEnd('/');
        }

        return Environment.GetEnvironmentVariable("TEAMSCOP_API_BASE")?.TrimEnd('/')
               ?? "https://teamscop.com";
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _timer.Stop();
        Interlocked.Increment(ref _loadGeneration);
        return ValueTask.CompletedTask;
    }
}
