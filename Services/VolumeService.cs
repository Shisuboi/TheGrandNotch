using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace TheGrandNotch.Services;

/// <summary>
/// Lit le volume système via Core Audio (IAudioEndpointVolume) et détecte les
/// pressions sur les touches média volume via un hook clavier bas niveau.
/// </summary>
public sealed class VolumeService : IDisposable
{
    // ── Core Audio COM ───────────────────────────────────────────────────────

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorClass { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    // Ordre du vtable conforme à endpointvolume.h du SDK Windows — NE PAS réordonner.
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        [PreserveSig] int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
        [PreserveSig] int VolumeStepUp(ref Guid pguidEventContext);
        [PreserveSig] int VolumeStepDown(ref Guid pguidEventContext);
        [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }

    // ── Hook clavier bas niveau ──────────────────────────────────────────────

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public int vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int VK_VOLUME_MUTE = 0xAD;
    private const int VK_VOLUME_DOWN = 0xAE;
    private const int VK_VOLUME_UP   = 0xAF;

    // ── État ─────────────────────────────────────────────────────────────────

    private IAudioEndpointVolume? _endpoint;
    private LowLevelKeyboardProc? _hookProc;   // référence GC-safe
    private IntPtr _hookHandle;
    private readonly Dispatcher _dispatcher;

    public float Volume  { get; private set; }
    public bool  IsMuted { get; private set; }

    /// <summary>Déclenché sur le thread UI après chaque changement de volume détecté.</summary>
    public event Action? VolumeChanged;

    // ── Init / Dispose ────────────────────────────────────────────────────────

    public VolumeService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        InitCoreAudio();
        InstallHook();
    }

    private void InitCoreAudio()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
            enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device);
            var iid = typeof(IAudioEndpointVolume).GUID;
            const uint CLSCTX_ALL = 23;
            device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
            _endpoint = (IAudioEndpointVolume)obj;
            ReadVolume();
        }
        catch { /* Core Audio indisponible — dégradation silencieuse */ }
    }

    private void InstallHook()
    {
        _hookProc = HookCallback;
        // hMod = IntPtr.Zero est valide pour WH_KEYBOARD_LL (hook global dans le même process)
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
    }

    private void ReadVolume()
    {
        if (_endpoint == null) return;
        try
        {
            _endpoint.GetMasterVolumeLevelScalar(out float level);
            _endpoint.GetMute(out bool muted);
            Volume  = level;
            IsMuted = muted;
        }
        catch { }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            var kbs = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (kbs.vkCode is VK_VOLUME_UP or VK_VOLUME_DOWN or VK_VOLUME_MUTE)
            {
                // 50 ms pour laisser Windows appliquer la modification avant la lecture
                _dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(50);
                    ReadVolume();
                    VolumeChanged?.Invoke();
                });
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        if (_endpoint != null)
        {
            Marshal.ReleaseComObject(_endpoint);
            _endpoint = null;
        }
    }
}
