using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TangerineWin;

/// <summary>
/// Global low-level mouse + keyboard hooks. Fires <see cref="DragStarted"/> when the user presses the left
/// button over Explorer / Desktop and moves the mouse a few pixels (a drag). While that drag is in progress,
/// tapping Shift or Ctrl fires <see cref="ModeKey"/>.
/// </summary>
internal sealed class DragHook : IDisposable
{
    private const int DragThresholdPx = 14;

    private readonly Native.HookProc _proc;   // keep references so the GC does not collect the delegates
    private readonly Native.HookProc _kbProc;
    private IntPtr _hook;
    private IntPtr _kbHook;
    private bool _shiftDown;
    private bool _ctrlDown;
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
    public event Action<WheelMode>? ModeKey;

    public DragHook()
    {
        _proc = HookCallback;
        _kbProc = KeyboardCallback;
        IntPtr module = Native.GetModuleHandle(null);
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, module, 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install mouse hook (error " + Marshal.GetLastWin32Error() + ")");
        _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, module, 0);
        if (_kbHook == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install keyboard hook (error " + Marshal.GetLastWin32Error() + ")");
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            int msg = wParam.ToInt32();
            int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is the first field
            bool isShift = vk == 0x10 || vk == 0xA0 || vk == 0xA1;
            bool isCtrl = vk == 0x11 || vk == 0xA2 || vk == 0xA3;

            if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
            {
                // ignore auto-repeat: only the first key-down counts as a "tap"
                if (isShift && !_shiftDown)
                {
                    _shiftDown = true;
                    if (_fired) ModeKey?.Invoke(WheelMode.Convert);
                }
                else if (isCtrl && !_ctrlDown)
                {
                    _ctrlDown = true;
                    if (_fired) ModeKey?.Invoke(WheelMode.Tools);
                }
            }
            else if (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP)
            {
                if (isShift) _shiftDown = false;
                if (isCtrl) _ctrlDown = false;
            }
        }
        return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
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
                    _armed = IsExplorerAt(info.pt);
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
        if (_kbHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_kbHook);
            _kbHook = IntPtr.Zero;
        }
    }
}
