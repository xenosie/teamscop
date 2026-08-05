using Teamscop.Engine.Usb;

namespace Teamscop.Api.Tests;

public class UsbSessionControllerTests
{
    [Fact]
    public async Task Insert_Blocks_Approve_Unlocks_Remove_Reblocks()
    {
        var policy = new NullRemovableStoragePolicy();
        var watcher = new PollingUsbDeviceWatcher();
        var prompt = new ScriptedUsbApprovalPrompt("654321");
        var verifier = new ScriptedUsbVerifier(succeed: true);

        await using var controller = new UsbSessionController(
            policy,
            watcher,
            prompt,
            verifier,
            deviceKey: () => "staff-device",
            apiBase: () => "https://teamscop.com");

        await controller.StartAsync();
        Assert.True(policy.IsBlocked);

        var device = new UsbStorageDevice("REMOVABLE\\E:", "TestStick", "E:", DateTimeOffset.UtcNow);
        watcher.SimulateArrival(device);

        await WaitUntilAsync(() => controller.State == UsbSessionState.SessionAllowed, TimeSpan.FromSeconds(3));
        Assert.Equal(UsbSessionState.SessionAllowed, controller.State);
        Assert.False(policy.IsBlocked);
        Assert.Equal(device.InstanceId, controller.SessionInstanceId);
        Assert.Equal(1, verifier.Calls);

        watcher.SimulateRemoval(device.InstanceId);
        await WaitUntilAsync(() => controller.State == UsbSessionState.Idle, TimeSpan.FromSeconds(3));
        Assert.True(policy.IsBlocked);
        Assert.Null(controller.SessionInstanceId);
    }

    [Fact]
    public async Task BadCode_KeepsBlock()
    {
        var policy = new NullRemovableStoragePolicy();
        var watcher = new PollingUsbDeviceWatcher();
        var prompt = new ScriptedUsbApprovalPrompt("000000");
        var verifier = new ScriptedUsbVerifier(succeed: false);

        await using var controller = new UsbSessionController(
            policy,
            watcher,
            prompt,
            verifier,
            deviceKey: () => "staff-device",
            apiBase: () => "https://teamscop.com");

        await controller.StartAsync();
        watcher.SimulateArrival(new UsbStorageDevice("REMOVABLE\\F:", "Bad", "F:", DateTimeOffset.UtcNow));

        await Task.Delay(300);
        Assert.NotEqual(UsbSessionState.SessionAllowed, controller.State);
        Assert.True(policy.IsBlocked);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("Condition not met before timeout.");
    }
}

file sealed class ScriptedUsbApprovalPrompt(string? code) : IUsbApprovalPrompt
{
    public Task<string?> PromptForTotpAsync(UsbApprovalRequest request, CancellationToken ct = default)
        => Task.FromResult(code);
}

file sealed class ScriptedUsbVerifier(bool succeed) : IUsbAccessVerifier
{
    public int Calls { get; private set; }

    public Task ApproveSessionAsync(string deviceKey, string totpCode, string? deviceInstanceId, CancellationToken ct = default)
    {
        Calls++;
        if (!succeed)
        {
            throw new UnauthorizedAccessException("Invalid TOTP code.");
        }

        return Task.CompletedTask;
    }
}
