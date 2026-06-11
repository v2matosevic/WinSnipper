using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WinSnipper;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _glyph;

    public TrayIcon(Action onNewSnip, Action onSettings, Action onExit)
    {
        // Use the exe's embedded icon; fall back to the runtime-drawn glyph
        // (e.g. when running through the dotnet host).
        Icon? exeIcon = null;
        try { exeIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        _glyph = exeIcon ?? CreateIcon();

        var menu = new ContextMenuStrip();

        var snipItem = new ToolStripMenuItem("New snip") { ShortcutKeyDisplayString = Util.CurrentHotkeyDisplay };
        snipItem.Click += (_, _) => onNewSnip();
        menu.Items.Add(snipItem);

        var folderItem = new ToolStripMenuItem("Open snips folder");
        folderItem.Click += (_, _) => OpenSnipsFolder();
        menu.Items.Add(folderItem);

        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => onSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => onExit();
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = _glyph,
            Text = $"WinSnipper — {Util.CurrentHotkeyDisplay} to snip",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenSnipsFolder();

        Settings.Changed += () =>
        {
            snipItem.ShortcutKeyDisplayString = Util.CurrentHotkeyDisplay;
            _icon.Text = $"WinSnipper — {Util.CurrentHotkeyDisplay} to snip";
        };
    }

    public void ShowError(string message) =>
        _icon.ShowBalloonTip(5000, "WinSnipper", message, ToolTipIcon.Warning);

    private static void OpenSnipsFolder()
    {
        System.IO.Directory.CreateDirectory(Util.SnipsDir);
        Process.Start("explorer.exe", Util.SnipsDir);
    }

    /// <summary>Viewfinder glyph drawn at runtime so we ship no binary assets.</summary>
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(255, 38, 105, 220));
            g.FillEllipse(bg, 1, 1, 30, 30);

            using var pen = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            // viewfinder corner brackets
            g.DrawLines(pen, new[] { new PointF(9, 13), new PointF(9, 9), new PointF(13, 9) });
            g.DrawLines(pen, new[] { new PointF(19, 9), new PointF(23, 9), new PointF(23, 13) });
            g.DrawLines(pen, new[] { new PointF(23, 19), new PointF(23, 23), new PointF(19, 23) });
            g.DrawLines(pen, new[] { new PointF(13, 23), new PointF(9, 23), new PointF(9, 19) });

            using var dot = new SolidBrush(Color.FromArgb(255, 255, 90, 80));
            g.FillEllipse(dot, 13.5f, 13.5f, 5, 5);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
