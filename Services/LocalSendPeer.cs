namespace TheGrandNotch.Services;

/// <summary>Appareil LocalSend découvert sur le réseau local.</summary>
public record LocalSendPeer(
    string Alias,
    string Ip,
    int    Port,
    string DeviceType,
    string Fingerprint,
    string Protocol = "http")
{
    public string DeviceIconPath => DeviceType switch
    {
        "desktop" or "laptop" or "headless" =>
            "M21 2H3c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h7l-2 3v1h8v-1l-2-3h7c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm0 12H3V4h18v10z",
        _ =>
            "M17 1.01L7 1c-1.1 0-2 .9-2 2v18c0 1.1.9 2 2 2h10c1.1 0 2-.9 2-2V3c0-1.1-.9-1.99-2-1.99zM17 19H7V5h10v14z"
    };
}
