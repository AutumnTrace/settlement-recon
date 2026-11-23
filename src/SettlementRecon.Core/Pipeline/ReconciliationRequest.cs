using SettlementRecon.Formats;
using SettlementRecon.Ledger;

namespace SettlementRecon.Pipeline;

/// <summary>A named input that can be opened as a stream (a file on disk, an upload, a blob).</summary>
public sealed record InputFile(string Name, Func<CancellationToken, ValueTask<Stream>> Open)
{
    public static InputFile FromPath(string path) => new(
        Path.GetFileName(path),
        _ => ValueTask.FromResult<Stream>(new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 0, // PipeReader does the buffering.
                Options = FileOptions.SequentialScan,
            })));

    public static InputFile FromBytes(string name, byte[] content) =>
        new(name, _ => ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false)));
}

/// <summary>One settlement file and the format it is written in.</summary>
public sealed record SettlementInput(ISettlementFormat Format, InputFile File);

/// <summary>Everything one run needs: the ledger export and one or more settlement files (several per provider are fine).</summary>
public sealed record ReconciliationRequest(InputFile Ledger, LedgerFileFormat LedgerFormat, IReadOnlyList<SettlementInput> Settlements)
{
    public static ReconciliationRequest FromPaths(string ledgerPath, IEnumerable<(ISettlementFormat Format, string Path)> settlements) => new(
        InputFile.FromPath(ledgerPath),
        LedgerFormats.FromPath(ledgerPath),
        [.. settlements.Select(s => new SettlementInput(s.Format, InputFile.FromPath(s.Path)))]);
}
