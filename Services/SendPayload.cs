using System.IO;
using System.IO.Compression;
using System.Text;

namespace TheGrandNotch.Services;

/// <summary>Un élément à envoyer via LocalSend (fichier, dossier zippé, texte ou image presse-papier).</summary>
public record SendPayload(
    string Id,
    string FileName,
    long   Size,
    string MimeType,
    Func<Stream> OpenStream)
{
    // ── Constructeurs ──────────────────────────────────────────────────────

    /// <summary>Fichier ordinaire. <paramref name="displayName"/> remplace le nom affiché si fourni.</summary>
    public static SendPayload FromFile(string path, string? displayName = null)
    {
        var name = displayName ?? Path.GetFileName(path);
        var size = new FileInfo(path).Length;
        return new SendPayload(NewId(), name, size, GuessMime(name), () => File.OpenRead(path));
    }

    /// <summary>Dossier → zippé dans un fichier temporaire.</summary>
    public static SendPayload FromFolder(string path)
    {
        var zipName = new DirectoryInfo(path).Name + ".zip";
        var tmp     = Path.Combine(Path.GetTempPath(), $"TGN_{NewId()}.zip");
        if (File.Exists(tmp)) File.Delete(tmp);
        // includeBaseDirectory:true → le zip conserve le dossier racine
        ZipFile.CreateFromDirectory(path, tmp, CompressionLevel.Fastest, includeBaseDirectory: true);
        var size = new FileInfo(tmp).Length;
        return new SendPayload(NewId(), zipName, size, "application/zip", () => File.OpenRead(tmp));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public static string GuessMime(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png"            => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif"            => "image/gif",
            ".webp"           => "image/webp",
            ".pdf"            => "application/pdf",
            ".txt" or ".md"   => "text/plain",
            ".zip"            => "application/zip",
            ".mp4" or ".mov"  => "video/mp4",
            ".mp3"            => "audio/mpeg",
            _                 => "application/octet-stream"
        };
}
