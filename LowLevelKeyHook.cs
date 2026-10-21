using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScreenRec;

/// <summary>
/// 低级键盘钩子（WH_KEYBOARD_LL）。
///
/// 为什么必须是它：
///   · WPF 的 KeyDown 在中文输入法激活时会被 IME 吞掉（Key.ImeProcessed），真实按键丢失；
///   · WPF 的 HwndSource.AddHook 排在「输入处理」之后，IME 消费过的键同样到不了；
///   · 低级钩子由 **OS 在消息投递之前**调用，完全在 WPF 之外，输入法拦不住。
///
/// 安全约束：只有当**本进程拥有前台窗口**时才动作，
/// 所以绝不会抢别的程序（浏览器、编辑器）的按键。
/// </summary>
internal sealed class LowLevelKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly HookProc _proc;      // 必须保持引用，否则委托被 GC 回收后钩子会崩
    private readonly uint _ownPid;
    private IntPtr _hook;
    private bool _disposed;

    /// <summary>按下的键（虚拟键码）。只在「本进程拥有前台窗口」时触发。</summary>
    public event Action<int>? KeyDown;

    public LowLevelKeyHook()
    {
        _ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        _proc = OnKey;
        try
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        }
        catch
        {
            _hook = IntPtr.Zero;
        }
    }

    public bool Installed => _hook != IntPtr.Zero;

    private IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam.ToInt32() == WM_KEYDOWN || wParam.ToInt32() == WM_SYSKEYDOWN))
            {
                // KBDLLHOOKSTRUCT 的第一个字段就是 vkCode
                int vk = Marshal.ReadInt32(lParam);
                if (OwnsForeground())
                {
                    KeyDown?.Invoke(vk);
                }
            }
        }
        catch
        {
            // 钩子里绝不能抛异常
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>前台窗口是不是本进程的（主面板 / 悬浮条 / 框选遮罩层都算）。</summary>
    private bool OwnsForeground()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            return pid == _ownPid;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
        catch
        {
            // ignore
        }
    }
}
