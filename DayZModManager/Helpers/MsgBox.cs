using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DayZModManager.Helpers;

internal static class MsgBox
{
    public static Task Info(Window? parent, string msg, string title = "DayZ Mod Manager")
        => ShowAsync(parent, title, msg, yesNo: false);

    public static async Task<bool> Confirm(Window? parent, string msg, string title = "DayZ Mod Manager")
    {
        bool result = false;
        var dlg = MakeWindow(title, msg, yesNo: true, r => result = r);
        if (parent != null) await dlg.ShowDialog(parent); else dlg.Show();
        return result;
    }

    private static async Task ShowAsync(Window? parent, string title, string msg, bool yesNo)
    {
        var dlg = MakeWindow(title, msg, yesNo, null);
        if (parent != null) await dlg.ShowDialog(parent); else dlg.Show();
    }

    private static Window MakeWindow(string title, string msg, bool yesNo, System.Action<bool>? onClose)
    {
        var text = new TextBlock
        {
            Text = msg,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
            Foreground = new SolidColorBrush(Color.Parse("#E4E4E7")),
            Margin = new Thickness(0, 0, 0, 16),
        };

        var btnOk = new Button
        {
            Content = yesNo ? "Yes" : "OK",
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        btns.Children.Add(btnOk);

        if (yesNo)
        {
            var btnNo = new Button { Content = "No", Width = 80 };
            btns.Children.Add(btnNo);
            // btnNo click is wired after win is created (see below)
            btnNo.Tag = "no";
        }

        var panel = new StackPanel { Spacing = 0, Margin = new Thickness(24) };
        panel.Children.Add(text);
        panel.Children.Add(btns);

        Window? win = null;
        win = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 280,
            MinHeight = 100,
            Background = new SolidColorBrush(Color.Parse("#0D0D10")),
            CanResize = false,
        };

        btnOk.Click += (_, _) => { onClose?.Invoke(true); win!.Close(); };
        if (yesNo)
        {
            var noBtn = btns.Children[1] as Button;
            if (noBtn != null) noBtn.Click += (_, _) => { onClose?.Invoke(false); win!.Close(); };
        }

        return win;
    }
}
