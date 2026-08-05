namespace Teamscop.Engine.Usb;

public interface IUsbApprovalPrompt
{
    /// <summary>Show the USB sticker and return a 6-digit TOTP (or null if cancelled).</summary>
    Task<string?> PromptForTotpAsync(UsbApprovalRequest request, CancellationToken ct = default);
}
