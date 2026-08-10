using Teamscop.App.ViewModels;
using Teamscop.Engine.Tracking;

namespace Teamscop.App.Tests;

/// <summary>
/// §3.4 — the rebuilt screenshot viewer. The gallery hands the request up so the viewer opens as a
/// shell-level full-screen overlay (over the nav, section nav and title bar), and Back/Esc closes it.
/// </summary>
public class ScreenshotViewerTests
{
    [Fact]
    public void ClickingAThumbnail_RaisesOpenViewer_WithThePageAndStartIndex()
        => AppTestHost.Run(async () =>
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var api = new StubApi();
            api.Screenshots.Add(Meta(first));
            api.Screenshots.Add(Meta(second));
            using var host = TestServices.SignedIn(api);
            var gallery = new ScreenshotGalleryViewModel(host.Services);

            IReadOnlyList<ScreenshotMetaItem>? page = null;
            var startIndex = -1;
            gallery.OpenViewerRequested += (metas, index) =>
            {
                page = metas;
                startIndex = index;
            };

            await gallery.LoadAsync(Guid.NewGuid(), force: true);
            var row = (ScreenshotRowViewModel)gallery.Rows[0];
            gallery.OpenViewer(row.Tiles[1]);

            Assert.NotNull(page);
            Assert.Equal(2, page!.Count);
            Assert.Equal(1, startIndex);
            Assert.Equal(second, page[1].Id);
        });

    [Fact]
    public void OpeningATile_ShowsTheFullScreenViewer_AndClosingClearsIt()
        => AppTestHost.Run(async () =>
        {
            var api = new StubApi();
            api.Screenshots.Add(Meta(Guid.NewGuid()));
            using var host = TestServices.SignedIn(api, role: "admin");
            await using var shell = new ShellViewModel(host.Services);

            var gallery = shell.StaffDetail.Screenshots;
            await gallery.LoadAsync(Guid.NewGuid(), force: true);
            var row = (ScreenshotRowViewModel)gallery.Rows[0];
            gallery.OpenViewer(row.Tiles[0]);

            Assert.True(shell.IsScreenshotViewerOpen);
            Assert.NotNull(shell.ScreenshotViewer);

            // Back / Esc both raise CloseRequested, which the shell handles by clearing the overlay.
            shell.ScreenshotViewer!.CloseCommand.Execute(null);

            Assert.False(shell.IsScreenshotViewerOpen);
            Assert.Null(shell.ScreenshotViewer);
        });

    private static ScreenshotMetaItem Meta(Guid id) => new()
    {
        Id = id,
        StaffUserId = Guid.NewGuid(),
        OccurredAt = DateTimeOffset.UtcNow,
        DisplayCount = 1,
        Displays = [new ScreenshotDisplayMeta { Index = 1, Width = 1280, Height = 720, Size = 4096 }]
    };
}
