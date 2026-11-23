using System.ComponentModel.DataAnnotations;

namespace SettlementRecon.Worker.Inbox;

/// <summary>
/// The worker's folders, all under one root so moves between them are atomic renames on the same volume:
/// <c>inbox/</c> (drop run folders here), <c>processing/</c>, <c>processed/</c> and <c>failed/</c>.
/// </summary>
public sealed class InboxOptions
{
    public const string SectionName = "Inbox";

    [Required]
    public string Root { get; set; } = "data";

    [Range(typeof(TimeSpan), "00:00:00.050", "01:00:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public string InboxPath => Path.GetFullPath(Path.Combine(Root, "inbox"));

    public string ProcessingPath => Path.GetFullPath(Path.Combine(Root, "processing"));

    public string ProcessedPath => Path.GetFullPath(Path.Combine(Root, "processed"));

    public string FailedPath => Path.GetFullPath(Path.Combine(Root, "failed"));

    public void EnsureFolders()
    {
        Directory.CreateDirectory(InboxPath);
        Directory.CreateDirectory(ProcessingPath);
        Directory.CreateDirectory(ProcessedPath);
        Directory.CreateDirectory(FailedPath);
    }
}

public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    /// <summary>Drop a generated sample run into an empty inbox at startup, so a fresh <c>dotnet run</c> has data to show.</summary>
    public bool SeedSampleRun { get; set; }

    [Range(1, 5_000_000)]
    public int Transactions { get; set; } = 10_000;

    public int Seed { get; set; } = 42;
}
