using System.Windows.Media;

namespace TheGrandNotch.Models;

public class CalendarEventItem
{
    public string Title { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public SolidColorBrush ColorBrush { get; init; } = new(Color.FromRgb(0x0A, 0x84, 0xFF));
    public bool IsAllDay { get; init; }

    public double EndedOpacity => End < DateTime.Now ? 0.5 : 1.0;

    public string TimeDisplay => IsAllDay
        ? "Toute la journée"
        : $"{Start:H:mm} – {End:H:mm}";
}
