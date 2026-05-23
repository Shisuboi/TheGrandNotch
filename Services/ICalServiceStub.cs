// Stub compilé uniquement par le projet wpftmp (génération XAML).
// Satisfait le compilateur sans dépendances NuGet Ical.Net.
namespace TheGrandNotch.Services;

public enum ICalStatus { Disconnected, Loading, Connected, Error }

public class ICalService
{
    public ICalStatus Status { get; private set; } = ICalStatus.Disconnected;
    public bool IsConfigured => false;
    public string StatusText => string.Empty;
    public event Action? StatusChanged;
    public void Load() { }
    public void SaveUrl(string url) { }
    public void Disconnect() { }
    public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Models.CalendarEventItem>> GetTodayEventsAsync()
        => System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<Models.CalendarEventItem>>([]);
    public System.Collections.Generic.IReadOnlyList<Models.CalendarEventItem> GetEventsForDate(System.DateTime date)
        => [];
}
