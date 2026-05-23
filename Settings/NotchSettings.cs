namespace TheGrandNotch.Settings;

public enum StartupMethod
{
    None,
    Registry,
    StartupFolder,
    ScheduledTask
}

public class NotchSettings
{
    public double Width { get; set; } = 220;
    public double Height { get; set; } = 36;
    public double ExpandedWidth { get; set; } = 640;
    public double ExpandedHeight { get; set; } = 190;
    public double CornerRadiusBottom { get; set; } = 18;
    public double ExpandedCornerRadius { get; set; } = 32;
    public double AnimationDurationMs { get; set; } = 320;
    public double EasingAmplitude { get; set; } = 0.20;
    public int TopmostReassertionMs { get; set; } = 200;
    public string BackgroundColor { get; set; } = "#FF000000";
    public double TopOffset { get; set; } = 0;
    public double MiniExtraWidth { get; set; } = 60;   // largeur ajoutée de chaque côté en mode mini actif
    public StartupMethod Startup { get; set; } = StartupMethod.None;
}
