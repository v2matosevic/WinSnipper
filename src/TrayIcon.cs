using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinSnipper;

public sealed class TrayIcon : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "WinSnipper";

    private readonly NotifyIcon _icon;
    private readonly Icon _glyph;

    public TrayIcon(Action onNewSnip, Action onExit)
    {
        _glyph = CreateIcon();

        var menu = new ContextMenuStrip();

        var snipItem = new ToolStripMenuItem("New snip") { ShortcutKeyDisplayString = "Win+Shift+S" };
        snipItem.Click += (_, _) => onNewSnip();
        menu.Items.Add(snipItem);

        var folderItem = new ToolStripMenuItem("Open snips folder");
        folderItem.Click += (_, _) => OpenSnipsFolder();
        menu.Items.Add(folderItem);

        menu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = IsStartupEnabled() };
        startupItem.CheckedChanged += (_, _) => SetStartup(startupItem.Checked);
        menu.Items.Add(startupItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => onExit();
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = _glyph,
            Text = "WinSnipper — Win+Shift+S to snip",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenSnipsFolder();
    }

    public void ShowError(string message) =>
        _icon.ShowBalloonTip(5000, "WinSnipper", message, ToolTipIcon.Warning);

    private static void OpenSnipsFolder()
    {
        System.IO.Directory.CreateDirectory(Util.SnipsDir);
        Process.Start("explorer.exe", Util.SnipsDir);
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
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
