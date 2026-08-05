using Avalonia.Controls;
using Avalonia.Input;

namespace Teamscop.App.Views;

public partial class TeamNameDialog : Window
{
    public string? TeamName { get; private set; }

    public TeamNameDialog()
    {
        InitializeComponent();
        BtnCancel.Click += (_, _) => Close(null);
        BtnCreate.Click += (_, _) => Accept();
        NameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Accept();
            }
        };
        Opened += (_, _) => NameBox.Focus();
    }

    private void Accept()
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        TeamName = name;
        Close(name);
    }

    public static async Task<string?> PromptAsync(Window owner)
    {
        var dlg = new TeamNameDialog();
        return await dlg.ShowDialog<string?>(owner);
    }
}
