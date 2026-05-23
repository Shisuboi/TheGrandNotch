using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;

namespace TheGrandNotch.Services;

/// <summary>Représente un périphérique audio (rendu ou capture).</summary>
public sealed class AudioDevice
{
    public string     Id                  { get; init; } = "";
    public string     Name                { get; init; } = "";
    public bool       IsDefault           { get; init; }
    public bool       IsCapture           { get; init; }
    public string     DeviceIconPath      { get; init; } = SpeakerPath;

    // Propriété calculée pour le binding XAML (pas de converter nécessaire)
    public Visibility CheckmarkVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;

    // ── Icônes Material Design ───────────────────────────────────────────────
    public const string SpeakerPath    = "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02z";
    public const string HeadphonePath  = "M12 1c-4.97 0-9 4.03-9 9v7c0 1.1.9 2 2 2h1v-8H5v-1c0-3.87 3.13-7 7-7s7 3.13 7 7v1h-1v8h2c1.1 0 2-.9 2-2v-7c0-4.97-4.03-9-9-9z";
    public const string TvPath         = "M21 3H3c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h5v2h8v-2h5c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 14H3V5h18v12z";
    public const string MicrophonePath = "M12 14c1.66 0 3-1.34 3-3V5c0-1.66-1.34-3-3-3S9 3.34 9 5v6c0 1.66 1.34 3 3 3zm5.3-3c0 3-2.54 5.1-5.3 5.1S6.7 14 6.7 11H5c0 3.41 2.72 6.23 6 6.72V21h2v-3.28c3.28-.48 6-3.3 6-6.72h-1.7z";
}

/// <summary>
/// Énumère les périphériques audio de rendu (IMMDeviceEnumerator)
/// et bascule le défaut via IPolicyConfig (API non documentée, stable Win 7→11).
/// </summary>
public sealed class AudioDeviceService : IDisposable
{
    // ── COM : énumération ────────────────────────────────────────────────────

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    [ClassInterface(ClassInterfaceType.None)]
    private class MMDeviceEnumeratorCom { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int pcDevices);
        [PreserveSig] int Item(int nDevice, out IMMDevice ppDevice);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant propvar);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // PropVariant minimal : on lit uniquement VT_LPWSTR (vt==31)
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    // ── COM : IPolicyConfig (non documentée) ─────────────────────────────────
    // Ordre du vtable stable sur Windows 7 / 8 / 10 / 11.
    // SetDefaultEndpoint est à la position 11 (après les 3 IUnknown + 8 méthodes).

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string dev, IntPtr ppFmt);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string dev, bool bDef, IntPtr ppFmt);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string dev);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string dev, IntPtr pFmt, IntPtr pMixFmt);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string dev, bool bDef, out long defPeriod, out long minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string dev, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string dev, out uint mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string dev, uint mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string dev, bool bFx, IntPtr pKey, IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string dev, bool bFx, IntPtr pKey, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, uint role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string dev, bool visible);
    }

    // GUID Windows 10/11 — utilisé par EarTrumpet et les switchers modernes
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CPolicyConfigClient { }

    // Fallback GUID (Vista/Win7) au cas où le premier échoue
    [ComImport, Guid("1F401FEA-8076-449D-AD08-2B7E0F946C7F")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CPolicyConfigClientLegacy { }

    // PKEY_Device_FriendlyName = {A45C254E-DF1C-4EFD-8020-67D146A850E0}, propId=14
    private static readonly PropertyKey PKEY_FriendlyName = new()
    {
        FormatId   = new Guid("{A45C254E-DF1C-4EFD-8020-67D146A850E0}"),
        PropertyId = 14
    };

    private const int DEVICE_STATE_ACTIVE = 1;

    // ── Public ───────────────────────────────────────────────────────────────

    public ObservableCollection<AudioDevice> Devices        { get; } = new();
    public ObservableCollection<AudioDevice> CaptureDevices { get; } = new();

    /// <summary>False si IPolicyConfig est inaccessible (dégradation silencieuse).</summary>
    public bool CanSwitch { get; private set; } = true;

    /// <summary>
    /// Ré-énumère les périphériques audio de rendu ET de capture actifs.
    /// Doit être appelé depuis le thread UI.
    /// </summary>
    public void Refresh()
    {
        Devices.Clear();
        CaptureDevices.Clear();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

            EnumerateFlow(enumerator, 0 /* eRender  */, Devices,        isCapture: false);
            EnumerateFlow(enumerator, 1 /* eCapture */, CaptureDevices, isCapture: true);
        }
        catch { /* COM non disponible — listes vides */ }
    }

    private void EnumerateFlow(IMMDeviceEnumerator enumerator, int dataFlow,
                                ObservableCollection<AudioDevice> target, bool isCapture)
    {
        string? defaultId = null;
        if (enumerator.GetDefaultAudioEndpoint(dataFlow, 1 /* eMultimedia */, out var defaultDev) == 0)
            defaultDev.GetId(out defaultId);

        if (enumerator.EnumAudioEndpoints(dataFlow, DEVICE_STATE_ACTIVE, out var collection) != 0) return;

        collection.GetCount(out int count);
        for (int i = 0; i < count; i++)
        {
            if (collection.Item(i, out var dev) != 0) continue;
            dev.GetId(out string id);
            string name = GetFriendlyName(dev);
            string icon = isCapture ? DetectCaptureIcon(name) : DetectRenderIcon(name);
            target.Add(new AudioDevice { Id = id, Name = name, IsDefault = id == defaultId,
                                         IsCapture = isCapture, DeviceIconPath = icon });
        }
    }

    /// <summary>
    /// Définit <paramref name="deviceId"/> comme périphérique par défaut pour tous les rôles audio.
    /// Fonctionne aussi bien pour les devices de rendu que de capture.
    /// </summary>
    /// <returns>True si au moins une des commandes a réussi.</returns>
    public bool SetDefault(string deviceId)
    {
        try
        {
            IPolicyConfig policy;
            try   { policy = (IPolicyConfig)new CPolicyConfigClient(); }
            catch { policy = (IPolicyConfig)new CPolicyConfigClientLegacy(); }

            int hr0 = policy.SetDefaultEndpoint(deviceId, 0 /* eConsole */);
            int hr1 = policy.SetDefaultEndpoint(deviceId, 1 /* eMultimedia */);
            int hr2 = policy.SetDefaultEndpoint(deviceId, 2 /* eCommunications */);

            bool ok = hr0 == 0 || hr1 == 0 || hr2 == 0;
            if (!ok) CanSwitch = false;
            return ok;
        }
        catch
        {
            CanSwitch = false;
            return false;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string GetFriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) == 0)
            {
                var key = PKEY_FriendlyName;
                if (store.GetValue(ref key, out var pv) == 0
                    && pv.vt == 31 /* VT_LPWSTR */
                    && pv.pwszVal != IntPtr.Zero)
                {
                    return Marshal.PtrToStringUni(pv.pwszVal) ?? "Audio Device";
                }
            }
        }
        catch { }
        return "Audio Device";
    }

    private static string DetectRenderIcon(string name) =>
        ContainsAny(name, "headphone", "headset", "casque", "écouteur", "airpod", "earphone", "bud", "wh-", "wf-", "bt ")
            ? AudioDevice.HeadphonePath
            : ContainsAny(name, "hdmi", "displayport", "television", "ecran", "écran")
                ? AudioDevice.TvPath
                : AudioDevice.SpeakerPath;

    private static string DetectCaptureIcon(string name) => AudioDevice.MicrophonePath;

    private static bool ContainsAny(string s, params string[] kws)
        => kws.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

    public void Dispose() { }
}
