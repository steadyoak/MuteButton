using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MuteButton;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        using var app = new TrayAppContext();
        Application.Run(app);
    }
}

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly Icon _appIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly Control _uiInvoker;
    private readonly MouseHook _mouseHook;

    public TrayAppContext()
    {
        _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        _uiInvoker = new Control();
        _uiInvoker.CreateControl();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "MuteButton - middle-click volume icon to mute",
            Visible = true,
            ContextMenuStrip = menu
        };

        _mouseHook = new MouseHook(OnMiddleClick);
        _mouseHook.Start();
    }

    private bool OnMiddleClick(Point screenPoint)
    {
        if (!TaskbarVolumeHitTester.IsVolumeIcon(screenPoint))
        {
            return false;
        }

        // Schedule audio COM toggle outside the low-level mouse hook callback.
        _uiInvoker.BeginInvoke(ToggleMuteSafe);
        return true;
    }

    private static void ToggleMuteSafe()
    {
        try
        {
            AudioEndpoint.ToggleMute();
        }
        catch
        {
            // Ignore transient audio endpoint failures so the tray hook keeps running.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _mouseHook.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _uiInvoker.Dispose();
            _appIcon.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class MouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;

    private readonly LowLevelMouseProc _proc;
    private readonly Func<Point, bool> _onMiddleDown;
    private IntPtr _hook;
    private bool _suppressNextMiddleUp;

    public MouseHook(Func<Point, bool> onMiddleDown)
    {
        _onMiddleDown = onMiddleDown;
        _proc = HookCallback;
    }

    public void Start()
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        var moduleHandle = NativeMethods.GetModuleHandle(currentModule?.ModuleName);
        _hook = NativeMethods.SetWindowsHookEx(WH_MOUSE_LL, _proc, moduleHandle, 0);

        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            if (wParam == WM_MBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (_onMiddleDown(new Point(info.pt.x, info.pt.y)))
                {
                    _suppressNextMiddleUp = true;
                    return new IntPtr(1);
                }

                _suppressNextMiddleUp = false;
            }
            else if (wParam == WM_MBUTTONUP && _suppressNextMiddleUp)
            {
                // Swallow the matching button-up so Windows does not treat an unpaired up event as a click.
                _suppressNextMiddleUp = false;
                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
}

internal static class TaskbarVolumeHitTester
{
    private const string SystemTrayIconId = "SystemTrayIcon";
    private const string VolumeButtonClass = "SystemTray.OmniButtonRight";
    private const string NetworkButtonClass = "SystemTray.AccentButton";

    private static readonly string[] SystemTrayButtonClasses =
    [
        VolumeButtonClass,
        "SystemTray.OmniButton",
        NetworkButtonClass
    ];

    private static Uia3.IUIAutomation? _uia;
    private static Uia3.IUIAutomationTreeWalker? _rawWalker;

    public static bool IsVolumeIcon(Point screenPoint)
    {
        var hwnd = NativeMethods.WindowFromPoint(new POINT(screenPoint.X, screenPoint.Y));
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var taskbarHwnd = FindTaskbarAncestor(hwnd);
        if (taskbarHwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            EnsureUia3Initialized();
            if (_uia is null || _rawWalker is null)
            {
                return false;
            }

            var hr = _uia.ElementFromPoint(new POINT(screenPoint.X, screenPoint.Y), out var element);
            if (hr != 0 || element is null)
            {
                return false;
            }

            var current = element;
            for (var depth = 0; current is not null && depth < 6; depth++)
            {
                current.get_CurrentClassName(out var className);
                current.get_CurrentAutomationId(out var automationId);
                current.get_CurrentName(out var name);

                if (IsSystemTrayCandidate(automationId, className))
                {
                    return MatchesVolumeTarget(taskbarHwnd, className!, name);
                }

                if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                {
                    break;
                }

                _rawWalker.GetParentElement(current, out var parent);
                current = parent;
            }
        }
        catch
        {
            // Ignore transient UIA errors during shell updates.
        }

        return false;
    }

    private static bool MatchesVolumeTarget(IntPtr taskbarHwnd, string className, string? elementName)
    {
        // Primary: language-agnostic match against the default audio render device friendly name from CoreAudio.
        var deviceName = AudioEndpoint.TryGetDefaultRenderFriendlyName();
        if (!string.IsNullOrWhiteSpace(deviceName)
            && !string.IsNullOrWhiteSpace(elementName)
            && elementName.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Secondary fallback (Scheme 1): dedicated Windows 11 volume/quick-settings right pill.
        if (className.Equals(VolumeButtonClass, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Tertiary fallback (Scheme 2): if OmniButtonRight does not exist in this taskbar layout, allow AccentButton too.
        return className.Equals(NetworkButtonClass, StringComparison.OrdinalIgnoreCase)
            && !TaskbarHasOmniButtonRight(taskbarHwnd);
    }

    private static bool TaskbarHasOmniButtonRight(IntPtr taskbarHwnd)
    {
        if (_uia is null)
        {
            return false;
        }

        var hr = _uia.ElementFromHandle(taskbarHwnd, out var root);
        if (hr != 0 || root is null)
        {
            return false;
        }

        _uia.CreateTrueCondition(out var trueCondition);
        if (trueCondition == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            root.FindAll(4 /* TreeScope_Descendants */, trueCondition, out var array);
            if (array is null)
            {
                return false;
            }

            array.get_Length(out var length);
            for (var i = 0; i < length; i++)
            {
                array.GetElement(i, out var item);
                if (item is null)
                {
                    continue;
                }

                item.get_CurrentAutomationId(out var id);
                item.get_CurrentClassName(out var cls);
                if (string.Equals(id, SystemTrayIconId, StringComparison.Ordinal)
                    && string.Equals(cls, VolumeButtonClass, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        finally
        {
            Marshal.Release(trueCondition);
        }

        return false;
    }

    private static IntPtr FindTaskbarAncestor(IntPtr hwnd)
    {
        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        var rootClass = NativeMethods.GetClassName(root);
        if (rootClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return root;
        }

        for (var current = hwnd; current != IntPtr.Zero; current = NativeMethods.GetParent(current))
        {
            var className = NativeMethods.GetClassName(current);
            if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                return current;
            }
        }

        return IntPtr.Zero;
    }

    private static void EnsureUia3Initialized()
    {
        if (_uia is not null && _rawWalker is not null)
        {
            return;
        }

        _uia = (Uia3.IUIAutomation)new Uia3.CUIAutomation8();
        _uia.get_RawViewWalker(out _rawWalker);
    }

    private static bool IsSystemTrayCandidate(string? automationId, string? className)
    {
        return string.Equals(automationId, SystemTrayIconId, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(className)
            && SystemTrayButtonClasses.Any(cls => className.Equals(cls, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class Uia3
{
    [ComImport]
    [Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
    public class CUIAutomation8
    {
    }

    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomation
    {
        int CompareElements(IntPtr el1, IntPtr el2, out int areSame);
        int CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);
        int GetRootElement(out IUIAutomationElement root);
        int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
        int ElementFromPoint(POINT pt, out IUIAutomationElement element);
        int GetFocusedElement(out IUIAutomationElement element);
        int GetRootElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement root);
        int ElementFromHandleBuildCache(IntPtr hwnd, IntPtr cacheRequest, out IUIAutomationElement element);
        int ElementFromPointBuildCache(POINT pt, IntPtr cacheRequest, out IUIAutomationElement element);
        int GetFocusedElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement element);
        int CreateTreeWalker(IntPtr pCondition, out IUIAutomationTreeWalker walker);
        int get_ControlViewWalker(out IUIAutomationTreeWalker walker);
        int get_ContentViewWalker(out IUIAutomationTreeWalker walker);
        int get_RawViewWalker(out IUIAutomationTreeWalker walker);
        int get_RawViewCondition(out IntPtr condition);
        int get_ControlViewCondition(out IntPtr condition);
        int get_ContentViewCondition(out IntPtr condition);
        int CreateCacheRequest(out IntPtr cacheRequest);
        int CreateTrueCondition(out IntPtr newCondition);
    }

    [ComImport]
    [Guid("4042C624-389C-4AFC-A630-9DF854A541FC")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationTreeWalker
    {
        int GetParentElement(IUIAutomationElement element, out IUIAutomationElement parent);
        int GetFirstChildElement(IUIAutomationElement element, out IUIAutomationElement first);
        int GetLastChildElement(IUIAutomationElement element, out IUIAutomationElement last);
        int GetNextSiblingElement(IUIAutomationElement element, out IUIAutomationElement next);
        int GetPreviousSiblingElement(IUIAutomationElement element, out IUIAutomationElement prev);
    }

    [ComImport]
    [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationElement
    {
        int SetFocus();
        int GetRuntimeId(out IntPtr runtimeId);
        int FindFirst(int scope, IntPtr condition, out IUIAutomationElement found);
        int FindAll(int scope, IntPtr condition, out IUIAutomationElementArray found);
        int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IUIAutomationElement found);
        int FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IUIAutomationElementArray found);
        int BuildUpdatedCache(IntPtr cacheRequest, out IUIAutomationElement updatedElement);
        int GetCurrentPropertyValue(int propertyId, out object retVal);
        int GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, out object retVal);
        int GetCachedPropertyValue(int propertyId, out object retVal);
        int GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue, out object retVal);
        int GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        int GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        int GetCurrentPattern(int patternId, out IntPtr patternObject);
        int GetCachedPattern(int patternId, out IntPtr patternObject);
        int GetCachedParent(out IUIAutomationElement parent);
        int GetCachedChildren(out IUIAutomationElementArray children);
        int get_CurrentProcessId(out int retVal);
        int get_CurrentControlType(out int retVal);
        int get_CurrentLocalizedControlType([MarshalAs(UnmanagedType.BStr)] out string retVal);
        int get_CurrentName([MarshalAs(UnmanagedType.BStr)] out string retVal);
        int get_CurrentAcceleratorKey([MarshalAs(UnmanagedType.BStr)] out string retVal);
        int get_CurrentAccessKey([MarshalAs(UnmanagedType.BStr)] out string retVal);
        int get_CurrentHasKeyboardFocus(out int retVal);
        int get_CurrentIsKeyboardFocusable(out int retVal);
        int get_CurrentIsEnabled(out int retVal);
        int get_CurrentAutomationId([MarshalAs(UnmanagedType.BStr)] out string retVal);
        int get_CurrentClassName([MarshalAs(UnmanagedType.BStr)] out string retVal);
    }

    [ComImport]
    [Guid("14314595-B4BC-4055-95F2-58F2E42C9855")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationElementArray
    {
        int get_Length(out int length);
        int GetElement(int index, out IUIAutomationElement element);
    }
}

internal static class AudioEndpoint
{
    private static readonly Guid DeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IAudioEndpointVolumeGuid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly PROPERTYKEY DeviceFriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private const uint StgmRead = 0;
    private const ushort VtLpWstr = 31;

    public static string? TryGetDefaultRenderFriendlyName()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IPropertyStore? store = null;
        var propVariant = new PROPVARIANT();

        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(DeviceEnumeratorClsid, throwOnError: true)!;
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device) != 0 || device is null)
            {
                return null;
            }

            if (device.OpenPropertyStore(StgmRead, out store) != 0 || store is null)
            {
                return null;
            }

            var key = DeviceFriendlyNameKey;
            if (store.GetValue(ref key, out propVariant) != 0 || propVariant.vt != VtLpWstr || propVariant.p1 == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(propVariant.p1);
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.PropVariantClear(ref propVariant);
            if (store is not null) Marshal.ReleaseComObject(store);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    public static void ToggleMute()
    {
        var enumeratorType = Type.GetTypeFromCLSID(DeviceEnumeratorClsid, throwOnError: true)!;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device));

        object? endpointObject = null;
        try
        {
            var endpointVolumeId = IAudioEndpointVolumeGuid;
            Marshal.ThrowExceptionForHR(device.Activate(ref endpointVolumeId, CLSCTX.CLSCTX_INPROC_SERVER, IntPtr.Zero, out endpointObject));
            var volume = (IAudioEndpointVolume)endpointObject;
            Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
            var context = Guid.Empty;
            Marshal.ThrowExceptionForHR(volume.SetMute(!muted, ref context));
        }
        finally
        {
            if (endpointObject is not null)
            {
                Marshal.ReleaseComObject(endpointObject);
            }

            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }
}

internal enum EDataFlow
{
    eRender,
    eCapture,
    eAll
}

internal enum ERole
{
    eConsole,
    eMultimedia,
    eCommunications
}

[Flags]
internal enum CLSCTX : uint
{
    CLSCTX_INPROC_SERVER = 0x1
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, CLSCTX dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);

    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    int GetState(out uint pdwState);
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    int GetCount(out uint cProps);
    int GetAt(uint iProp, out PROPERTYKEY pkey);

    [PreserveSig]
    int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    int RegisterControlChangeNotify(IntPtr pNotify);
    int UnregisterControlChangeNotify(IntPtr pNotify);
    int GetChannelCount(out uint pnChannelCount);
    int SetMasterVolumeLevel(float fLevelDB, Guid pguidEventContext);
    int SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);
    int GetMasterVolumeLevel(out float pfLevelDB);
    int GetMasterVolumeLevelScalar(out float pfLevel);
    int SetChannelVolumeLevel(uint nChannel, float fLevelDB, Guid pguidEventContext);
    int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, Guid pguidEventContext);
    int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
    int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
    int VolumeStepUp(Guid pguidEventContext);
    int VolumeStepDown(Guid pguidEventContext);
    int QueryHardwareSupport(out uint pdwHardwareSupportMask);
    int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
}

internal static class NativeMethods
{
    public const uint GA_ROOT = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, MouseHook.LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameRaw(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    public static string GetClassName(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        var length = GetClassNameRaw(hwnd, builder, builder.Capacity);
        return length > 0 ? builder.ToString() : string.Empty;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT(int x, int y)
{
    public int x = x;
    public int y = y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY(Guid fmtid, uint pid)
{
    public Guid fmtid = fmtid;
    public uint pid = pid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public ushort vt;
    public ushort r1;
    public ushort r2;
    public ushort r3;
    public IntPtr p1;
    public IntPtr p2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSLLHOOKSTRUCT
{
    public POINT pt;
    public uint mouseData;
    public uint flags;
    public uint time;
    public IntPtr dwExtraInfo;
}
