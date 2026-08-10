using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Teamscop.Setup.Ui;

/// <summary>
/// The install dashboard. The pipeline runs on a background thread and reports here; the window
/// only renders. Closing is disabled while the pipeline runs — a half-copied bin\ is the worst
/// outcome an installer can produce, and the close button coming back is itself the signal that
/// it is safe to leave.
/// </summary>
public partial class InstallerWindow : Window
{
    private static readonly IBrush StepDone = SolidColorBrush.Parse("#16A34A");
    private static readonly IBrush StepActive = SolidColorBrush.Parse("#2563EB");
    private static readonly IBrush StepPending = SolidColorBrush.Parse("#94A3B8");

    private readonly List<(TextBlock Mark, TextBlock Label)> _steps = [];
    private bool _running = true;
    private bool _succeeded;

    public int ResultExitCode { get; private set; } = 1;

    public InstallerWindow()
        : this(_ => 1, "")
    {
    }

    public InstallerWindow(Func<IProgress<InstallProgress>, int> work, string versionLabel)
    {
        InitializeComponent();
        VersionText.Text = string.IsNullOrWhiteSpace(versionLabel) ? "Setup" : "Setup " + versionLabel;

        foreach (var title in InstallSteps.Titles)
        {
            var mark = new TextBlock
            {
                Text = "○",
                FontSize = 13,
                Foreground = StepPending,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var label = new TextBlock
            {
                Text = title,
                FontSize = 13,
                Foreground = StepPending,
                Margin = new Avalonia.Thickness(8, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            row.Children.Add(mark);
            row.Children.Add(label);
            StepList.Children.Add(row);
            _steps.Add((mark, label));
        }

        var progress = new Progress<InstallProgress>(Render);
        _ = Task.Run(() =>
        {
            int code;
            string? error = null;
            try
            {
                code = work(progress);
            }
            catch (Exception ex)
            {
                code = 1;
                error = ex.Message;
            }

            Dispatcher.UIThread.Post(() => Finish(code, error));
        });
    }

    private void Render(InstallProgress p)
    {
        for (var i = 0; i < _steps.Count; i++)
        {
            var (mark, label) = _steps[i];
            if (i < p.StepIndex)
            {
                mark.Text = "✓";
                mark.Foreground = StepDone;
                label.Foreground = StepDone;
            }
            else if (i == p.StepIndex)
            {
                mark.Text = "●";
                mark.Foreground = StepActive;
                label.Foreground = StepActive;
                label.FontWeight = FontWeight.SemiBold;
            }
            else
            {
                mark.Text = "○";
                mark.Foreground = StepPending;
                label.Foreground = StepPending;
                label.FontWeight = FontWeight.Normal;
            }
        }

        StageTitle.Text = InstallSteps.Titles[Math.Clamp(p.StepIndex, 0, InstallSteps.Titles.Length - 1)];
        StageDetail.Text = p.Detail;
        Progress.Value = Math.Clamp(p.Percent, 0, 100);
        PercentText.Text = $"{(int)Math.Clamp(p.Percent, 0, 100)}%";
    }

    private void Finish(int code, string? error)
    {
        _running = false;
        _succeeded = code == 0;
        ResultExitCode = code;

        if (_succeeded)
        {
            foreach (var (mark, label) in _steps)
            {
                mark.Text = "✓";
                mark.Foreground = StepDone;
                label.Foreground = StepDone;
            }

            Progress.Value = 100;
            PercentText.Text = "100%";
            StageTitle.Text = "Complete";
            StageDetail.Text = "";
            DonePanel.IsVisible = true;
            FooterText.Text = VersionText.Text;
            ActionText.Text = "Finish";
        }
        else
        {
            StageTitle.Text = "Failed";
            ErrorText.Text = error ?? "Unknown error. Run the installer again.";
            ErrorPanel.IsVisible = true;
            FooterText.Text = "Nothing more will be changed on this PC.";
            ActionText.Text = "Close";
        }

        ActionButton.IsVisible = true;
    }

    private void OnActionClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_succeeded)
        {
            // The Finish click is the go signal: only now does the app launch and the flow move on.
            try { InstallerApp.OnFinished?.Invoke(); }
            catch { /* launching the app is best effort; the install itself is complete */ }
        }

        Close();
    }

    private void OnCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // While the pipeline runs the close button is a no-op rather than hidden: hiding it makes
        // people reach for Task Manager, which is precisely the partial-install disaster this
        // window exists to prevent.
        if (!_running)
        {
            Close();
        }
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
