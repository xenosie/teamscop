using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Teamscop.Engine.Lifecycle;

namespace Teamscop.UninstallGuard.Views;

public partial class UninstallStickerWindow : Window
{
    private readonly IApprovalCodeVerifier? _verifier;
    private readonly Func<Task>? _auditAsync;
    private bool _busy;

    /// <summary>0 = authorized, 1 = failed, 2 = cancelled.</summary>
    public int ResultExitCode { get; private set; } = 2;

    /// <summary>Designer constructor. A window with no verifier can never authorize anything.</summary>
    public UninstallStickerWindow()
        : this(null, null)
    {
    }

    public UninstallStickerWindow(IApprovalCodeVerifier? verifier, Func<Task>? auditAsync)
    {
        _verifier = verifier;
        _auditAsync = auditAsync;
        InitializeComponent();
        Opened += (_, _) =>
        {
            PositionNearBottomRight();
            CodeBox.Focus();
        };
        Closing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true;
            }
        };
    }

    private void PositionNearBottomRight()
    {
        var screen = Screens.Primary?.WorkingArea
                     ?? new PixelRect(0, 0, 1280, 720);
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
            _ = SubmitAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelAndClose();
        }
        else
        {
            // Clear the last error the moment the user starts a new attempt.
            ClearError();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void ClearError()
    {
        if (ErrorText.IsVisible)
        {
            ErrorText.IsVisible = false;
            ErrorText.Text = string.Empty;
        }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => _ = SubmitAsync();

    private void CancelAndClose()
    {
        ResultExitCode = 2;
        Close();
    }

    private async Task SubmitAsync()
    {
        if (_busy)
        {
            return;
        }

        var code = (CodeBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(code))
        {
            CancelAndClose();
            return;
        }

        _busy = true;
        OkButton.IsEnabled = false;
        CodeBox.IsEnabled = false;

        // A6 / §11.2 — decided here, on this machine, with no server call.
        var check = _verifier?.Verify(ApprovalPurpose.Uninstall, code)
                    ?? ApprovalCheck.Deny(ApprovalRefusal.NoSecret);
        if (!check.Ok)
        {
            _busy = false;
            OkButton.IsEnabled = true;
            CodeBox.IsEnabled = true;
            CodeBox.Text = string.Empty;
            // Tell the user WHY. The refusal used to be computed and thrown away — every code
            // bounced behind a 700 ms red border with no explanation, which read as "the
            // password-based uninstall was never implemented" (defect 1).
            ShowError(check.Describe());
            FlashError();
            CodeBox.Focus();
            return;
        }

        ClearError();

        if (_auditAsync is not null)
        {
            try
            {
                await _auditAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The uninstall was authorised; failing to record it must not block removal.
            }
        }

        ResultExitCode = 0;
        _busy = false;
        Close();
    }

    private void FlashError()
    {
        Chrome.BorderBrush = new SolidColorBrush(Color.Parse("#DC2626"));
        DispatcherTimer.RunOnce(
            () => Chrome.BorderBrush = new SolidColorBrush(Color.Parse("#2563EB")),
            TimeSpan.FromMilliseconds(700));
    }
}
