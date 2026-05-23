namespace TheGrandNotch.Services;

public record IncomingTransferFile(string Id, string FileName, long Size, string MimeType);

/// <summary>Demande de transfert entrant d'un appareil LocalSend.</summary>
public sealed class IncomingTransfer
{
    private readonly TaskCompletionSource<bool> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string SessionId   { get; }
    public string SenderAlias { get; }
    public IReadOnlyList<IncomingTransferFile> Files { get; }

    public IncomingTransfer(string sessionId, string senderAlias,
                             IReadOnlyList<IncomingTransferFile> files)
    {
        SessionId   = sessionId;
        SenderAlias = senderAlias;
        Files       = files;
    }

    public string FileSummary => Files.Count switch
    {
        0 => "(aucun fichier)",
        1 => Files[0].FileName,
        _ => $"{Files[0].FileName} et {Files.Count - 1} autre{(Files.Count > 2 ? "s" : "")}"
    };

    public string SizeSummary
    {
        get
        {
            long t = Files.Sum(f => f.Size);
            return t >= 1_048_576 ? $"{t / 1_048_576.0:F1} Mo"
                 : t >= 1_024     ? $"{t / 1024.0:F0} Ko"
                 :                  $"{t} o";
        }
    }

    internal Task<bool> WaitForDecisionAsync(CancellationToken ct) => _tcs.Task.WaitAsync(ct);
    public void Accept()  => _tcs.TrySetResult(true);
    public void Decline() => _tcs.TrySetResult(false);
}
