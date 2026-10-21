using System.Runtime.InteropServices;

namespace ScreenRec;

/// <summary>
/// 原生按键辅助。
///
/// 为什么需要它：WPF 的 KeyDown 在中文输入法（IME）激活时，
/// 被 IME 处理的键会变成 Key.ImeProcessed（229），真实按键就丢了 ——
/// 于是「Esc 取消」「R 重新框选」在打中文时全都失灵。
/// 这里直接读 Win32 虚拟键码，绕开 IME。
/// </summary>
internal static class RawKeys
{
    public const int VK_BACK = 0x08;
    public const int VK_TAB = 0x09;
    public const int VK_RETURN = 0x0D;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;   // Alt
    public const int VK_ESCAPE = 0x1B;
    public const int VK_SPACE = 0x20;
    public const int VK_R = 0x52;

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vKey);

    /// <summary>是否按着 Shift / Ctrl / Alt（有修饰键时按下的字母键不做处理，避免抢 Ctrl+R 这类组合）。</summary>
    public static bool ModifierDown()
    {
        return (GetKeyState(VK_CONTROL) & 0x8000) != 0
            || (GetKeyState(VK_MENU) & 0x8000) != 0
            || (GetKeyState(VK_SHIFT) & 0x8000) != 0;
    }

    /// <summary>把虚拟键码转成可读名字（仅用于日志）。</summary>
    public static string Name(int vk)
    {
        return vk switch
        {
            VK_ESCAPE => "Esc",
            VK_RETURN => "Enter",
            VK_R => "R",
            VK_SPACE => "Space",
            VK_BACK => "Backspace",
            VK_TAB => "Tab",
            _ => $"VK_{vk:X2}"
        };
    }
}
