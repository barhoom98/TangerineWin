using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Registry = Microsoft.Win32.Registry;

namespace TangerineWin;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "TangerineWin.SingleInstance", out bool created);
        if (!created) return;

        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        using var controller = new Controller(app);
        app.Run();
    }
}

internal sealed class Controller : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "TangerineWin";

    private readonly System.Windows.Application _app;
    private readonly DragHook _hook;
    private readonly RadialWindow _ring;
    private readonly Forms.NotifyIcon _tray;
    private string? _pendingSelectPath;
    private int _session;
    private bool _dragging;
    private bool _fileDrag;   // UI Automation confirmed the drag started on a file/folder item

    public Controller(System.Windows.Application app)
    {
        _app = app;
        _ring = new RadialWindow();
        _ring.ActionChosen += OnActionChosen;

        _hook = new DragHook();
        _hook.DragStarted += OnDragStarted;
        _hook.ButtonReleased += OnButtonReleased;
        _hook.ModeKey += OnModeKey;

        _tray = BuildTray();
    }

    // ---- drag lifecycle ---------------------------------------------------------------------

    private void OnDragStarted(int startX, int startY)
    {
        int session = ++_session;
        _dragging = true;
        _fileDrag = false;

        // UI Automation is a cross-process call: do it off the hook thread.
        Task.Run(() => IsFileItemAt(startX, startY)).ContinueWith(t =>
        {
            if (!t.Result) return;
            _app.Dispatcher.BeginInvoke(() =>
            {
                if (session != _session || !_dragging) return;
                _fileDrag = true;

                // the user may already be holding Shift / Ctrl
                WheelMode? held = HeldMode();
                if (held != null) _ring.ShowOrSwitch(_hook.LastX, _hook.LastY, held.Value);
            });
        });
    }

    /// <summary>The user tapped Shift (convert) or Ctrl (tools) during a drag.</summary>
    private void OnModeKey(WheelMode mode)
    {
        _app.Dispatcher.BeginInvoke(() =>
        {
            if (_dragging && _fileDrag) _ring.ShowOrSwitch(_hook.LastX, _hook.LastY, mode);
        });
    }

    private static WheelMode? HeldMode()
    {
        if ((Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0) return WheelMode.Convert;
        if ((Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0) return WheelMode.Tools;
        return null;
    }

    private void OnButtonReleased()
    {
        _dragging = false;
        _fileDrag = false;
        int session = _session;
        // Give the OLE Drop event time to arrive before we hide the ring.
        _app.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(500);
            if (session == _session) _ring.HideRing();
        });
    }

    private static bool IsFileItemAt(int x, int y)
    {
        try
        {
            AutomationElement? el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            TreeWalker walker = TreeWalker.ControlViewWalker;
            for (int i = 0; el != null && i < 4; i++)
            {
                ControlType type = el.Current.ControlType;
                if (type == ControlType.ListItem || type == ControlType.DataItem) return true;
                el = walker.GetParent(el);
            }
            return false;
        }
        catch
        {
            return true; // if UI Automation fails, prefer showing the ring
        }
    }

    // ---- running actions --------------------------------------------------------------------

    private async void OnActionChosen(ActionDef action, string[] files)
    {
        // let the drag-and-drop finish before we open any window or start heavy work
        await Task.Delay(80);
        if (!action.Interactive) Notify("🍊 " + action.Label, "جاري المعالجة…", null);
        try
        {
            ActionResult r = await action.Run(files);
            Notify("🍊 " + action.Label, r.Message, r.Path);
        }
        catch (Exception ex)
        {
            Notify("🍊 " + action.Label, "خطأ: " + ex.Message, null);
        }
    }

    private void Notify(string title, string text, string? selectPath)
    {
        _pendingSelectPath = selectPath;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _tray.ShowBalloonTip(4000);
    }

    // ---- tray -------------------------------------------------------------------------------

    private Forms.NotifyIcon BuildTray()
    {
        var menu = new Forms.ContextMenuStrip();

        var enabled = new Forms.ToolStripMenuItem("مفعّل") { Checked = true, CheckOnClick = true };
        enabled.CheckedChanged += (_, _) => _hook.Enabled = enabled.Checked;
        menu.Items.Add(enabled);

        var autostart = new Forms.ToolStripMenuItem("التشغيل مع ويندوز") { Checked = IsAutoStart(), CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => SetAutoStart(autostart.Checked);
        menu.Items.Add(autostart);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("خروج", null, (_, _) => _app.Shutdown());

        var icon = new Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Tangerine – اسحب ملف ثم اضغط Shift أو Ctrl",
            Visible = true,
            ContextMenuStrip = menu,
        };
        icon.BalloonTipClicked += (_, _) =>
        {
            if (_pendingSelectPath != null && (File.Exists(_pendingSelectPath) || Directory.Exists(_pendingSelectPath)))
                Process.Start("explorer.exe", $"/select,\"{_pendingSelectPath}\"");
        };
        return icon;
    }

    private static Drawing.Icon CreateIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var orange = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 138, 31));
            using var green = new Drawing.SolidBrush(Drawing.Color.FromArgb(67, 160, 71));
            g.FillEllipse(orange, 3, 6, 26, 25);
            g.FillEllipse(green, 15, 1, 11, 7);
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Drawing.Icon.FromHandle(h);
            return (Drawing.Icon)tmp.Clone();
        }
        finally
        {
            Native.DestroyIcon(h);
        }
    }

    private static bool IsAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(RunValue) != null;
    }

    private static void SetAutoStart(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;
        if (on) key.SetValue(RunValue, "\"" + Environment.ProcessPath + "\"");
        else key.DeleteValue(RunValue, false);
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _hook.Dispose();
        _ring.Close();
    }
}
