using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteConnect.Platform.MacOS;

[SupportedOSPlatform("macos")]
internal static class MacNative
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    // CGBitmapInfo: kCGImageAlphaNoneSkipFirst | kCGBitmapByteOrder32Little → BGRX in memory.
    public const uint BitmapInfoBgrx = 6u | (2u << 12);
    public const int InterpolationMedium = 4;

    public const int EventSourceStateHidSystem = 1;
    public const int HidEventTap = 0;

    public const int EventLeftMouseDown = 1;
    public const int EventLeftMouseUp = 2;
    public const int EventRightMouseDown = 3;
    public const int EventRightMouseUp = 4;
    public const int EventMouseMoved = 5;
    public const int EventLeftMouseDragged = 6;
    public const int EventRightMouseDragged = 7;
    public const int EventKeyDown = 10;
    public const int EventKeyUp = 11;
    public const int EventFlagsChanged = 12;
    public const int EventScrollWheel = 22;
    public const int EventOtherMouseDown = 25;
    public const int EventOtherMouseUp = 26;
    public const int EventOtherMouseDragged = 27;

    public const int MouseButtonLeft = 0;
    public const int MouseButtonRight = 1;
    public const int MouseButtonCenter = 2;

    public const int FieldMouseEventClickState = 1;
    public const int FieldScrollWheelDeltaAxis1 = 11;
    public const int FieldScrollWheelDeltaAxis2 = 12;
    public const int ScrollUnitLine = 1;

    public const ulong FlagAlphaShift = 0x00010000;
    public const ulong FlagShift = 0x00020000;
    public const ulong FlagControl = 0x00040000;
    public const ulong FlagAlternate = 0x00080000;
    public const ulong FlagCommand = 0x00100000;

    [StructLayout(LayoutKind.Sequential)]
    public struct CGPoint
    {
        public double X;
        public double Y;

        public CGPoint(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;

        public CGRect(double x, double y, double w, double h)
        {
            X = x;
            Y = y;
            Width = w;
            Height = h;
        }
    }

    // ---- Displays & capture ----
    [DllImport(CoreGraphics)]
    public static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[] activeDisplays, out uint displayCount);

    [DllImport(CoreGraphics)]
    public static extern uint CGMainDisplayID();

    [DllImport(CoreGraphics)]
    public static extern CGRect CGDisplayBounds(uint display);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphics)]
    public static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);

    [DllImport(CoreGraphics)]
    public static extern nuint CGDisplayModeGetPixelHeight(IntPtr mode);

    [DllImport(CoreGraphics)]
    public static extern void CGDisplayModeRelease(IntPtr mode);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGDisplayCreateImage(uint displayId);

    [DllImport(CoreGraphics)]
    public static extern nuint CGImageGetWidth(IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern nuint CGImageGetHeight(IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern void CGImageRelease(IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGColorSpaceCreateDeviceRGB();

    [DllImport(CoreGraphics)]
    public static extern void CGColorSpaceRelease(IntPtr space);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGBitmapContextCreate(IntPtr data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, IntPtr space, uint bitmapInfo);

    [DllImport(CoreGraphics)]
    public static extern void CGContextDrawImage(IntPtr context, CGRect rect, IntPtr image);

    [DllImport(CoreGraphics)]
    public static extern void CGContextSetInterpolationQuality(IntPtr context, int quality);

    [DllImport(CoreGraphics)]
    public static extern void CGContextRelease(IntPtr context);

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CGPreflightScreenCaptureAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CGRequestScreenCaptureAccess();

    // ---- Events ----
    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventSourceCreate(int stateId);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport(CoreGraphics)]
    public static extern CGPoint CGEventGetLocation(IntPtr evt);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreateMouseEvent(IntPtr source, int mouseType, CGPoint mouseCursorPosition, int mouseButton);

    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.U1)] bool keyDown);

    /// <summary>Non-variadic scroll constructor (macOS 13+). The variadic CGEventCreateScrollWheelEvent is not P/Invoke safe on arm64.</summary>
    [DllImport(CoreGraphics)]
    public static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, int units, uint wheelCount, int wheel1, int wheel2, int wheel3);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetType(IntPtr evt, int type);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetLocation(IntPtr evt, CGPoint location);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetIntegerValueField(IntPtr evt, int field, long value);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetFlags(IntPtr evt, ulong flags);

    [DllImport(CoreGraphics)]
    public static extern void CGEventPost(int tap, IntPtr evt);

    // ---- CoreFoundation ----
    [DllImport(CoreFoundation)]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundation)]
    public static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);

    // ---- Accessibility (input control permission) ----
    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool AXIsProcessTrusted();

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

    /// <summary>Shows the system "allow accessibility" prompt (adds the app to the list in System Settings).</summary>
    public static bool PromptAccessibility()
    {
        IntPtr appServices = NativeLibrary.Load(ApplicationServices);
        IntPtr coreFoundation = NativeLibrary.Load(CoreFoundation);
        IntPtr promptKey = Marshal.ReadIntPtr(NativeLibrary.GetExport(appServices, "kAXTrustedCheckOptionPrompt"));
        IntPtr trueValue = Marshal.ReadIntPtr(NativeLibrary.GetExport(coreFoundation, "kCFBooleanTrue"));
        IntPtr keyCallbacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks");
        IntPtr valueCallbacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks");
        IntPtr dict = CFDictionaryCreate(IntPtr.Zero, new[] { promptKey }, new[] { trueValue }, 1, keyCallbacks, valueCallbacks);
        try
        {
            return AXIsProcessTrustedWithOptions(dict);
        }
        finally
        {
            if (dict != IntPtr.Zero)
            {
                CFRelease(dict);
            }
        }
    }

    public static CGPoint CurrentMouseLocation()
    {
        IntPtr evt = CGEventCreate(IntPtr.Zero);
        if (evt == IntPtr.Zero)
        {
            return default;
        }

        try
        {
            return CGEventGetLocation(evt);
        }
        finally
        {
            CFRelease(evt);
        }
    }
}
