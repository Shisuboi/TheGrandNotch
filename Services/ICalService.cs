using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using TheGrandNotch.Models;

namespace TheGrandNotch.Services;

public enum ICalStatus { Disconnected, Loading, Connected, Error }

public class ICalService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string UrlFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TheGrandNotch", "ical_url.txt");

    private string?   _url;
    private Calendar? _cachedCalendar;   // mis en cache au dernier GetTodayEventsAsync

    public ICalStatus Status { get; private set; } = ICalStatus.Disconnected;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_url);

    public string StatusText => Status switch
    {
        ICalStatus.Connected  => "Calendrier connecté",
        ICalStatus.Loading    => "Chargement…",
        ICalStatus.Error      => "Erreur de chargement",
        _                     => "Aucune source connectée"
    };

    public event Action? StatusChanged;

    public void Load()
    {
        if (!File.Exists(UrlFile)) return;
        var raw = File.ReadAllText(UrlFile).Trim();
        if (!string.IsNullOrEmpty(raw))
        {
            _url = raw;
            Status = ICalStatus.Connected;
        }
    }

    public void SaveUrl(string url)
    {
        _url = url.Trim();
        _cachedCalendar = null;   // forcer un rechargement réseau
        var dir = Path.GetDirectoryName(UrlFile)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(UrlFile, _url);
        Status = ICalStatus.Connected;
        StatusChanged?.Invoke();
    }

    public void Disconnect()
    {
        _url = null;
        _cachedCalendar = null;
        if (File.Exists(UrlFile)) File.Delete(UrlFile);
        Status = ICalStatus.Disconnected;
        StatusChanged?.Invoke();
    }

    /// <summary>Charge le .ics depuis le réseau, met en cache, retourne les événements d'aujourd'hui.</summary>
    public async Task<IReadOnlyList<CalendarEventItem>> GetTodayEventsAsync()
    {
        if (!IsConfigured) return [];

        Status = ICalStatus.Loading;
        StatusChanged?.Invoke();

        try
        {
            var content = await _http.GetStringAsync(_url);
            _cachedCalendar = Calendar.Load(content);

            Status = ICalStatus.Connected;
            StatusChanged?.Invoke();
            return GetEventsForDate(DateTime.Today);
        }
        catch
        {
            Status = ICalStatus.Error;
            StatusChanged?.Invoke();
            return [];
        }
    }

    /// <summary>Retourne les événements d'un jour donné depuis le cache — aucune requête réseau.</summary>
    public IReadOnlyList<CalendarEventItem> GetEventsForDate(DateTime date)
    {
        if (_cachedCalendar is null) return [];

        var rangeStart = new CalDateTime(date.Date);
        var rangeEnd   = new CalDateTime(date.Date.AddDays(1));

        return _cachedCalendar
            .GetOccurrences(rangeStart, rangeEnd)
            .Select(MapOccurrence)
            .Where(e => e is not null)
            .Select(e => e!)
            .OrderBy(e => e.Start)
            .ToList();
    }

    private static CalendarEventItem? MapOccurrence(Occurrence occ)
    {
        if (occ.Source is not CalendarEvent e) return null;

        bool allDay = e.IsAllDay;
        var start   = allDay
            ? occ.Period.StartTime.AsSystemLocal.Date
            : occ.Period.StartTime.AsSystemLocal;
        var end = allDay
            ? start.AddDays(1)
            : (occ.Period.EndTime?.AsSystemLocal ?? start.AddHours(1));

        return new CalendarEventItem
        {
            Title      = e.Summary ?? "(sans titre)",
            Start      = start,
            End        = end,
            IsAllDay   = allDay,
            ColorBrush = DefaultBrush
        };
    }

    private static readonly SolidColorBrush DefaultBrush =
        new(Color.FromRgb(0x0A, 0x84, 0xFF));
}
