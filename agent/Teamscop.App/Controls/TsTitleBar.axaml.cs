using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Teamscop.App.Controls;

public partial class TsTitleBar : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<TsTitleBar, string?>(nameof(Title), "Teamscop");

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public TsTitleBar()
    {
        InitializeComponent();
        DragArea.PointerPressed += OnDragPressed;
        BtnClose.Click += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is Window window)
            {
                window.Close();
            }
        };
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.BeginMoveDrag(e);
        }
    }
}
