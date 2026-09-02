namespace Shigure;

public sealed class KeySender : IRuntimeKeyOutput
{
    private readonly WowProcessLocator _processLocator;

    internal KeySender(WowProcessLocator processLocator)
    {
        _processLocator = processLocator;
    }

    public string? LastFailureReason { get; private set; }

    public bool Send(string hotkey)
    {
        var result = SendCore(hotkey, expectedWindow: 0);
        LastFailureReason = result.FailureReason;
        return result.Succeeded;
    }

    public static int? GetVk(string keyName) => WindowsVirtualKeyMap.Resolve(keyName);

    KeySendResult IRuntimeKeyOutput.Send(string hotkey, nint expectedWindow)
        => SendCore(hotkey, expectedWindow);

    private KeySendResult SendCore(string hotkey, nint expectedWindow)
    {
        var (mods, mainKey) = ParseHotkey(hotkey);
        if (mainKey is null)
        {
            return Fail($"无法解析按键“{hotkey}”");
        }

        var vkMain = WindowsVirtualKeyMap.Resolve(mainKey);
        if (vkMain is null)
        {
            return Fail($"无法识别主键“{mainKey}”");
        }

        var hwnd = _processLocator.FindFrontmostWindow();
        if (hwnd == 0)
        {
            return Fail($"未找到目标进程的可见窗口（wow_process.txt: {_processLocator.DescribeConfiguredProcesses()}）");
        }

        if (expectedWindow != 0 && hwnd != expectedWindow)
        {
            return Fail("目标窗口已切换，等待重新扫描后再发送按键");
        }

        // ParseHotkey 只产出去重后的 CTRL/ALT/SHIFT, 三者都在虚拟键表里且映射到互异 VK,
        // 故 Resolve 不会为 null、结果天然去重。
        var modVks = mods.Select(m => WindowsVirtualKeyMap.Resolve(m)!.Value).ToList();

        var succeeded = true;
        var firstError = 0;
        var mainExtended = IsExtendedKey(mainKey);
        void SendMessage(int vk, bool keyUp, bool extended = false)
        {
            if (!Post(hwnd, vk, keyUp, extended, out var error))
            {
                succeeded = false;
                if (firstError == 0)
                {
                    firstError = error;
                }
            }
        }

        foreach (var vk in modVks)
        {
            SendMessage(vk, keyUp: false);
        }

        SendMessage(vkMain.Value, keyUp: false, mainExtended);
        SendMessage(vkMain.Value, keyUp: true, mainExtended);

        for (var i = modVks.Count - 1; i >= 0; i--)
        {
            SendMessage(modVks[i], keyUp: true);
        }

        if (succeeded)
        {
            return KeySendResult.Success;
        }

        return Fail(firstError == 5
            ? "权限不足（Win32 错误码 5）：请确认 Shigure 与魔兽世界使用相同的管理员权限运行"
            : $"向目标窗口发送按键失败，Win32 错误码: {firstError}");
    }

    private static (List<string> Mods, string? MainKey) ParseHotkey(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return (new List<string>(), null);
        }

        // 从左消费修饰前缀，剩余整段当主键。CTRL-- 不能按 '-' 切开，否则会丢掉减号。
        var remaining = hotkey.Trim();
        var mods = new List<string>();
        while (TryConsumeModifierPrefix(ref remaining, out var modifier))
        {
            if (!mods.Contains(modifier))
            {
                mods.Add(modifier);
            }
        }

        return remaining.Length == 0 ? (mods, null) : (mods, remaining);
    }

    private static bool TryConsumeModifierPrefix(ref string remaining, out string modifier)
    {
        if (StartsWithIgnoreCase(remaining, "CONTROL-"))
        {
            remaining = remaining["CONTROL-".Length..];
            modifier = "CTRL";
            return true;
        }

        if (StartsWithIgnoreCase(remaining, "CTRL-"))
        {
            remaining = remaining["CTRL-".Length..];
            modifier = "CTRL";
            return true;
        }

        if (StartsWithIgnoreCase(remaining, "MENU-"))
        {
            remaining = remaining["MENU-".Length..];
            modifier = "ALT";
            return true;
        }

        if (StartsWithIgnoreCase(remaining, "ALT-"))
        {
            remaining = remaining["ALT-".Length..];
            modifier = "ALT";
            return true;
        }

        if (StartsWithIgnoreCase(remaining, "SHIFT-"))
        {
            remaining = remaining["SHIFT-".Length..];
            modifier = "SHIFT";
            return true;
        }

        modifier = "";
        return false;
    }

    private static bool StartsWithIgnoreCase(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsExtendedKey(string keyName)
        => keyName.Equals("NUMPADDIVIDE", StringComparison.OrdinalIgnoreCase)
            || keyName.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
            || keyName.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
            || keyName.Equals("HOME", StringComparison.OrdinalIgnoreCase)
            || keyName.Equals("END", StringComparison.OrdinalIgnoreCase)
            || keyName.Equals("PAGEUP", StringComparison.OrdinalIgnoreCase)
            || keyName.Equals("PAGEDOWN", StringComparison.OrdinalIgnoreCase)
            || keyName.Equals("LEFT", StringComparison.OrdinalIgnoreCase)
            || keyName.Equals("UP", StringComparison.OrdinalIgnoreCase)
            || keyName.Equals("RIGHT", StringComparison.OrdinalIgnoreCase)
            || keyName.Equals("DOWN", StringComparison.OrdinalIgnoreCase);

    private static KeySendResult Fail(string reason) => KeySendResult.Failure(reason);

    private static bool Post(nint hwnd, int keyCode, bool keyUp, bool extended, out int error)
    {
        var scanCode = NativeMethods.MapVirtualKeyW((uint)keyCode, 0) & 0xFF;
        var value = 1u | (scanCode << 16);
        if (extended || keyCode == 0x6F) // VK_DIVIDE 与导航/方向键是扩展键。
        {
            value |= 1u << 24;
        }

        if (keyUp)
        {
            value |= (1u << 30) | (1u << 31);
        }

        var posted = NativeMethods.PostMessageW(
            hwnd,
            keyUp ? NativeMethods.WmKeyUp : NativeMethods.WmKeyDown,
            (nint)keyCode,
            unchecked((nint)(int)value));
        error = posted ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        return posted;
    }
}
