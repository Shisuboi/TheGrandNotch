using System.Windows.Media.Imaging;

namespace TheGrandNotch.Services;

/// <summary>Un fichier déposé sur l'étagère (page Boîte).</summary>
public class ShelfItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public BitmapSource? Icon { get; init; }
}
