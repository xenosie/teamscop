using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Teamscop.Engine.Usb;

namespace Teamscop.UsbApproval.Views;

/// <summary>
/// §7.3 — the always-on-top USB approval box: what is being asked, a 6-digit input, OK and Cancel.
///
/// It only COLLECTS the code and writes the response file. Verification is offline but happens in the
/// StaffService (LocalUsbAccessVerifier → LocalApprovalVerifier), which owns the single replay/lockout
/// watermark; a second verify here would consume the step and make the service's verify fail as a
/// replay. So the box is deliberately dumb — the service decides.
/// </summary>
public partial class UsbStickerWindow : Window
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly UsbApprovalRequest? _request;
    private readonly string? _responsePath;
    private bool _responded;

    /// <summary>0 = code submitted, 2 = cancelled.</summary>
    public int ResultExitCode { get; private set; } = 2;

    /// <summary>Designer constructor. A window with no request can only cancel.</summary>
    public UsbStickerWindow()
        : this(null, null)
    {
    }

    public UsbStickerWindow(UsbApprovalRequest? request, string? responsePath)
    {
        _request = request;
        _responsePath = responsePath;
        InitializeComponent();

        if (request is not null)
        {
            var name = request.Device.FriendlyName ?? request.Device.InstanceId;
            if (name.Length > 48)
            {
                name = name[..48];
            }

            DeviceText.Text = $"Device: {name}\nEnter the 6-digit code from your admin to use it.";
        }

        Opened += (_, _) =>
        {
            PositionNearBottomRight();
            CodeBox.Focus();
        };
    }

    private void PositionNearBottomRight()
    {
        var screen = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1280, 720);
        var scale = DesktopScaling <= 0 ? 1.0 : DesktopScaling;
        var w = (int)Math.Round(Width * scale);
        var h = (int)Math.Round(Height * scale);
        Position = new PixelPoint(
            screen.X + screen.Width - w - 24,
            screen.Y + screen.Height - h - 24);
    }

    private void OnChromePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && e.Source is not TextBox and not Button)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Submit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelAndClose();
        }
        else if (ErrorText.IsVisible)
        {
            ErrorText.IsVisible = false;
            ErrorText.Text = string.Empty;
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => Submit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => CancelAndClose();

    private void Submit()
    {
        var code = (CodeBox.Text ?? string.Empty).Trim();
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            ErrorText.Text = "Enter the 6-digit code.";
            ErrorText.IsVisible = true;
            CodeBox.Focus();
            return;
        }

        WriteResponse(approved: true, code: code);
        ResultExitCode = 0;
        Close();
    }

    private void CancelAndClose()
    {
        WriteResponse(approved: false, code: null);
        ResultExitCode = 2;
        Close();
    }

    private void WriteResponse(bool approved, string? code)
    {
        if (_responded || _request is null || string.IsNullOrWhiteSpace(_responsePath))
        {
            return;
        }

        _responded = true;
        try
        {
            var response = new UsbApprovalResponse(
                _request.RequestId, approved, code, approved ? null : "cancelled");
            File.WriteAllText(_responsePath, JsonSerializer.Serialize(response, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The service falls back to "no response" (device stays blocked), which is the safe
            // outcome — a stick nobody could approve simply does not mount.
        }
    }
}
