using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace Lantern.App;

/// <summary>
/// Tray presence and close behaviour.
///
/// History: this launcher used to keep running after the window closed while
/// showing no tray icon, so the only way to find it again was Task Manager.
/// The fix is both halves: a real tray icon, and an explicit choice between
/// "minimise to tray" and "quit", with quitting actually stopping the server we
/// started.
/// </summary>
public partial class MainWindow
{
    NotifyIcon? tray;
    bool exiting;

    void BuildTray()
    {
        tray = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "CS2 Lantern",
            Visible = true,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("显示主窗口", null, (_, _) => ShowFromTray()));
        menu.Items.Add(new ToolStripMenuItem("重启独立服务器…", null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ShowFromTray();
            if (!launching) _ = RestartDedicatedServer();
        })));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出（停止服务器）", null, (_, _) => Dispatcher.BeginInvoke(() => RequestExit())));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
    }

    static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/lantern.ico"));
            if (resource != null) using (var stream = resource.Stream) return new System.Drawing.Icon(stream);
        }
        catch (Exception) { /* Fall through to the shell icon. */ }
        return System.Drawing.SystemIcons.Application;
    }

    void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void HideToTray()
    {
        // Without this the window keeps a taskbar button while never being visible.
        ShowInTaskbar = false;
        Hide();
        if (tray != null)
        {
            tray.BalloonTipTitle = "CS2 Lantern 仍在运行";
            tray.BalloonTipText = "双击托盘图标可重新打开；右键可退出并停止服务器。";
            tray.ShowBalloonTip(2500);
        }
    }

    /// <summary>User chose to quit: stop the server we own, then actually close.</summary>
    async void RequestExit()
    {
        if (exiting) return;
        exiting = true;
        if (localServer != null)
        {
            Status("正在停止独立服务器…");
            var stopped = await localServer.Stop(TimeSpan.FromSeconds(15));
            Status(stopped ? "独立服务器已停止。" : "独立服务器可能仍在运行；已取消其控制台通道。");
            localServer.Dispose();
            localServer = null;
        }
        Close();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) { Cleanup(); return; }
        if (prefs.CloseToTray)
        {
            // Cancel and hide instead of closing: the same process keeps running,
            // but now the user can see it in the tray.
            e.Cancel = true;
            HideToTray();
            return;
        }
        // Quitting stops the server first; the second Close() call passes through.
        e.Cancel = true;
        RequestExit();
    }

    void Cleanup()
    {
        roomTimer.Stop(); mapTimer.Stop(); lifetime.Cancel();
        terminal?.Close();
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        localServer?.Dispose();
        localServer = null;
        if (ready) { try { Save(); } catch { } }
    }
}
