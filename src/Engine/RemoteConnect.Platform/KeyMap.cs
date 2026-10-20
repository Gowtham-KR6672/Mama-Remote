namespace RemoteConnect.Platform;

/// <summary>
/// One physical key: USB HID usage (keyboard page 0x07) mapped to the Windows PS/2 set-1 scan code
/// (or a virtual-key code where scan codes are unreliable) and the macOS virtual key code (kVK_*).
/// </summary>
public readonly record struct KeyMapping(ushort Hid, string Name, ushort WinScan, bool WinExtended, ushort WinVk, short MacKey);

public static class KeyMap
{
    public const short NoMacKey = -1;

    // Modifier HID usages.
    public const ushort LeftCtrl = 0xE0, LeftShift = 0xE1, LeftAlt = 0xE2, LeftMeta = 0xE3;
    public const ushort RightCtrl = 0xE4, RightShift = 0xE5, RightAlt = 0xE6, RightMeta = 0xE7;
    public const ushort CapsLock = 0x39;

    private static readonly Dictionary<ushort, KeyMapping> ByHid = Build();

    public static IReadOnlyCollection<KeyMapping> All => ByHid.Values;

    public static bool TryGet(ushort hid, out KeyMapping mapping) => ByHid.TryGetValue(hid, out mapping);

    public static bool IsModifier(ushort hid) => hid is >= 0xE0 and <= 0xE7;

    private static Dictionary<ushort, KeyMapping> Build()
    {
        var d = new Dictionary<ushort, KeyMapping>();

        void Add(ushort hid, string name, ushort scan, bool ext, short mac, ushort vk = 0)
            => d.Add(hid, new KeyMapping(hid, name, scan, ext, vk, mac));

        // Letters A..Z (HID 0x04..0x1D)
        ushort[] letterScan = { 0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C };
        short[] letterMac = { 0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E, 0x2D, 0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06 };
        for (int i = 0; i < 26; i++)
        {
            Add((ushort)(0x04 + i), ((char)('A' + i)).ToString(), letterScan[i], false, letterMac[i]);
        }

        // Digits 1..9, 0 (HID 0x1E..0x27)
        short[] digitMac = { 0x12, 0x13, 0x14, 0x15, 0x17, 0x16, 0x1A, 0x1C, 0x19, 0x1D };
        for (int i = 0; i < 10; i++)
        {
            Add((ushort)(0x1E + i), i == 9 ? "Digit0" : $"Digit{i + 1}", (ushort)(0x02 + i), false, digitMac[i]);
        }

        Add(0x28, "Enter", 0x1C, false, 0x24);
        Add(0x29, "Escape", 0x01, false, 0x35);
        Add(0x2A, "Backspace", 0x0E, false, 0x33);
        Add(0x2B, "Tab", 0x0F, false, 0x30);
        Add(0x2C, "Space", 0x39, false, 0x31);
        Add(0x2D, "Minus", 0x0C, false, 0x1B);
        Add(0x2E, "Equal", 0x0D, false, 0x18);
        Add(0x2F, "BracketLeft", 0x1A, false, 0x21);
        Add(0x30, "BracketRight", 0x1B, false, 0x1E);
        Add(0x31, "Backslash", 0x2B, false, 0x2A);
        Add(0x32, "NonUSHash", 0x2B, false, 0x2A);
        Add(0x33, "Semicolon", 0x27, false, 0x29);
        Add(0x34, "Quote", 0x28, false, 0x27);
        Add(0x35, "Backquote", 0x29, false, 0x32);
        Add(0x36, "Comma", 0x33, false, 0x2B);
        Add(0x37, "Period", 0x34, false, 0x2F);
        Add(0x38, "Slash", 0x35, false, 0x2C);
        Add(CapsLock, "CapsLock", 0x3A, false, 0x39);

        // F1..F12
        ushort[] fScan = { 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58 };
        short[] fMac = { 0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65, 0x6D, 0x67, 0x6F };
        for (int i = 0; i < 12; i++)
        {
            Add((ushort)(0x3A + i), $"F{i + 1}", fScan[i], false, fMac[i]);
        }

        // Keys without a direct macOS equivalent map to F13..F15 like Apple's own keyboards.
        Add(0x46, "PrintScreen", 0x37, true, 0x69, vk: 0x2C);
        Add(0x47, "ScrollLock", 0x46, false, 0x6B);
        Add(0x48, "Pause", 0x45, false, 0x71, vk: 0x13);
        Add(0x49, "Insert", 0x52, true, 0x72);
        Add(0x4A, "Home", 0x47, true, 0x73);
        Add(0x4B, "PageUp", 0x49, true, 0x74);
        Add(0x4C, "Delete", 0x53, true, 0x75);
        Add(0x4D, "End", 0x4F, true, 0x77);
        Add(0x4E, "PageDown", 0x51, true, 0x79);
        Add(0x4F, "ArrowRight", 0x4D, true, 0x7C);
        Add(0x50, "ArrowLeft", 0x4B, true, 0x7B);
        Add(0x51, "ArrowDown", 0x50, true, 0x7D);
        Add(0x52, "ArrowUp", 0x48, true, 0x7E);

        // Keypad
        Add(0x53, "NumLock", 0x45, false, 0x47, vk: 0x90);
        Add(0x54, "NumPadDivide", 0x35, true, 0x4B);
        Add(0x55, "NumPadMultiply", 0x37, false, 0x43);
        Add(0x56, "NumPadSubtract", 0x4A, false, 0x4E);
        Add(0x57, "NumPadAdd", 0x4E, false, 0x45);
        Add(0x58, "NumPadEnter", 0x1C, true, 0x4C);
        ushort[] kpScan = { 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49, 0x52 };
        short[] kpMac = { 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5B, 0x5C, 0x52 };
        for (int i = 0; i < 10; i++)
        {
            Add((ushort)(0x59 + i), i == 9 ? "NumPad0" : $"NumPad{i + 1}", kpScan[i], false, kpMac[i]);
        }

        Add(0x63, "NumPadDecimal", 0x53, false, 0x41);
        Add(0x64, "IntlBackslash", 0x56, false, 0x0A);
        Add(0x65, "ContextMenu", 0x5D, true, NoMacKey);
        Add(0x67, "NumPadEqual", 0x59, false, 0x51);

        // F13..F24
        ushort[] f13Scan = { 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x76 };
        short[] f13Mac = { 0x69, 0x6B, 0x71, 0x6A, 0x40, 0x4F, 0x50, 0x5A, NoMacKey, NoMacKey, NoMacKey, NoMacKey };
        for (int i = 0; i < 12; i++)
        {
            Add((ushort)(0x68 + i), $"F{i + 13}", f13Scan[i], false, f13Mac[i]);
        }

        Add(0x85, "NumPadComma", 0x7E, false, 0x5F);
        Add(0x87, "IntlRo", 0x73, false, 0x5E);
        Add(0x88, "KanaMode", 0x70, false, 0x68);
        Add(0x89, "IntlYen", 0x7D, false, 0x5D);
        Add(0x8A, "Convert", 0x79, false, NoMacKey);
        Add(0x8B, "NonConvert", 0x7B, false, NoMacKey);
        Add(0x90, "Lang1", 0x72, false, 0x68, vk: 0x15);
        Add(0x91, "Lang2", 0x71, false, 0x66, vk: 0x19);

        // Modifiers. Windows (GUI) key ↔ macOS Command, Alt ↔ Option.
        Add(LeftCtrl, "ControlLeft", 0x1D, false, 0x3B);
        Add(LeftShift, "ShiftLeft", 0x2A, false, 0x38);
        Add(LeftAlt, "AltLeft", 0x38, false, 0x3A);
        Add(LeftMeta, "MetaLeft", 0x5B, true, 0x37);
        Add(RightCtrl, "ControlRight", 0x1D, true, 0x3E);
        Add(RightShift, "ShiftRight", 0x36, false, 0x3C);
        Add(RightAlt, "AltRight", 0x38, true, 0x3D);
        Add(RightMeta, "MetaRight", 0x5C, true, 0x36);

        return d;
    }
}
