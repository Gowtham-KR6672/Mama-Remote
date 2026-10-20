using Avalonia.Input;
using RemoteConnect.Platform;

namespace RemoteConnect.Desktop.Services;

/// <summary>Avalonia physical key (W3C "code") → USB HID usage sent on the wire.</summary>
public static class KeyMapper
{
    private static readonly Dictionary<PhysicalKey, ushort> Map = Build();

    public static bool TryGetHid(PhysicalKey key, out ushort hid) => Map.TryGetValue(key, out hid);

    private static Dictionary<PhysicalKey, ushort> Build()
    {
        var map = new Dictionary<PhysicalKey, ushort>();
        foreach (var k in KeyMap.All)
        {
            if (Enum.TryParse<PhysicalKey>(k.Name, ignoreCase: false, out var pk) && pk != PhysicalKey.None)
            {
                map.TryAdd(pk, k.Hid);
            }
        }

        // The macOS "clear" key sits where NumLock is on PC keyboards.
        map.TryAdd(PhysicalKey.NumPadClear, 0x53);
        return map;
    }
}
