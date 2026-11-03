using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TangerineWin;

/// <summary>
/// Global low-level mouse hook. Fires <see cref="DragStarted"/> when the user holds Shift,
/// presses the left button over Explorer / Desktop and moves the mouse a few pixels (a drag).
/// </summary>
internal sealed class DragHook : IDisposable
{
    private const int DragThresholdPx = 14;

    private readonly Native.HookProc _proc; // keep a reference so the GC does not collect the delegate
    private IntPtr _hook;
    private bool _armed;
    private bool _fired;
    private int _startX;
    private int _startY;

    public bool Enabled { get; set; } = true;
    public int LastX { get; private set; }
    public int LastY { get; private set; }

    /// <summary>(startX, startY) in physical screen pixels.</summary>
    public event Action<int, int>? DragStarted;
    public event Action? ButtonReleased;

    public DragHook()
    {
        _proc = HookCallback;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install mouse hook (error " + Marshal.GetLastWin32Error() + ")");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            int msg = wParam.ToInt32();
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            LastX = info.pt.X;
            LastY = info.pt.Y;

            switch (msg)
            {
                case Native.WM_LBUTTONDOWN:
                    _fired = false;
                    _startX = info.pt.X;
                    _startY = info.pt.Y;
                    _armed = IsShiftDown() && IsExplorerAt(info.pt);
                    break;

                case Native.WM_MOUSEMOVE:
                    if (_armed && !_fired)
                    {
                        int dx = info.pt.X - _startX;
                        int dy = info.pt.Y - _startY;
                        if (dx * dx + dy * dy > DragThresholdPx * DragThresholdPx)
                        {
                            _fired = true;
                            DragStarted?.Invoke(_startX, _startY);
                        }
                    }
                    break;

                case Native.WM_LBUTTONUP:
                    bool wasFired = _fired;
                    _armed = false;
                    _fired = false;
                    if (wasFired) ButtonReleased?.Invoke();
                    break;
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsShiftDown() => (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;

    private static bool IsExplorerAt(Native.POINT pt)
    {
        try
        {
            IntPtr h = Native.WindowFromPoint(pt);
            if (h == IntPtr.Zero) return false;
            IntPtr root = Native.GetAncestor(h, Native.GA_ROOT);
            if (root == IntPtr.Zero) root = h;
            var sb = new StringBuilder(64);
            Native.GetClassName(root, sb, sb.Capacity);
            string cls = sb.ToString();
            // Explorer window, Desktop
            return cls == "CabinetWClass" || cls == "Progman" || cls == "WorkerW";
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
