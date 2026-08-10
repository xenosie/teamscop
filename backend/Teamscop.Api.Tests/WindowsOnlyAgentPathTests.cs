using Teamscop.Engine.Tracking;
using Teamscop.Engine.Usb;

namespace Teamscop.Api.Tests;

/// <summary>
/// Agent behaviour that cannot execute on Linux, written and skipped rather than left out.
///
/// These are the honest edges of C2. The USB gate is P/Invoke into SetupAPI, the floor is a
/// registry policy, and screen capture is GDI — off Windows every one of them is a stub that
/// answers "not supported", so a green run on Linux says nothing about §9.2 or §6.3. The tests exist
/// so that fact is visible as a skip in the run rather than as an absence nobody notices.
/// </summary>
public class WindowsOnlyAgentPathTests
{
    [WindowsOnlyFact(
        "SetupDi device-node disable and the removable-storage registry policy. Off Windows both "
        + "factories return the null stubs, so §9.2 (\"the device must not appear in the PC at all\") "
        + "has no coverage here at all — only the state machine above them does.")]
    public async Task TheDeviceGateAndTheStorageFloorAreSupported()
    {
        Assert.True(UsbSessionController.CreateGate().IsSupported);
        Assert.True(UsbSessionController.CreateFloor().IsSupported);

        // The polling watcher observes nothing on its own — it exists so the state machine can be
        // driven from a test. A real machine must get the WMI-backed one.
        await using var watcher = UsbSessionController.CreateWatcher();
        Assert.IsNotType<PollingUsbDeviceWatcher>(watcher);
    }

    [WindowsOnlyFact(
        "GDI multi-display capture. Off Windows ScreenshotEngine emits a 16x16 stub so the vault and "
        + "sync paths stay exercisable, which means nothing here proves §6.3 (\"all displays are "
        + "captured each cycle\").")]
    public void AFailedCaptureProducesNothing_RatherThanAFabricatedFrame()
    {
        var result = new ScreenshotEngine().CaptureAllDisplays(new StaffTrackingConfig { ScreenshotEnabled = true });

        // §6.1 makes screenshots proof of work. A placeholder frame standing in for a capture that
        // did not happen is worse than a gap, because the gap is reported and the placeholder is not.
        Assert.DoesNotContain(result.Displays, c => c is { Width: 16, Height: 16 });
        Assert.All(result.Displays, c => Assert.NotEmpty(c.WebpBytes));
    }
}
