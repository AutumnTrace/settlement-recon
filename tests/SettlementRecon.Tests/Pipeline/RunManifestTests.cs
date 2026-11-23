using SettlementRecon.Formats;
using SettlementRecon.Pipeline;
using SettlementRecon.Tests.TestSupport;

namespace SettlementRecon.Tests.Pipeline;

public class RunManifestTests
{
    [Test]
    public async Task A_manifest_resolves_to_files_inside_its_folder()
    {
        using var temp = new TempDirectory();
        temp.File("ledger.csv", "x");
        temp.File("orbit.csv", "x");
        await new RunManifest("ledger.csv", [new ManifestSettlement("orbit", "orbit.csv")], "test").WriteAsync(temp.Path);

        var manifest = await RunManifest.ReadAsync(temp.Path);
        var request = manifest.ToRequest(temp.Path, SettlementFormatRegistry.Default);

        Assert.That(manifest.Label, Is.EqualTo("test"));
        Assert.That(request.Ledger.Name, Is.EqualTo("ledger.csv"));
        Assert.That(request.Settlements.Single().Format.Provider, Is.EqualTo("orbit"));
    }

    [TestCase("../secrets.csv")]
    [TestCase("..\\secrets.csv")]
    [TestCase("/etc/passwd")]
    public void Paths_that_leave_the_run_folder_are_refused(string file)
    {
        using var temp = new TempDirectory();
        temp.File("ledger.csv", "x");
        var manifest = new RunManifest("ledger.csv", [new ManifestSettlement("orbit", file)]);

        Assert.That(() => manifest.ToRequest(temp.Path, SettlementFormatRegistry.Default),
            Throws.TypeOf<InvalidDataException>().Or.TypeOf<FileNotFoundException>());
    }

    [Test]
    public void Missing_files_and_unknown_providers_are_reported()
    {
        using var temp = new TempDirectory();
        temp.File("ledger.csv", "x");

        Assert.That(
            () => new RunManifest("ledger.csv", [new ManifestSettlement("orbit", "nope.csv")]).ToRequest(temp.Path, SettlementFormatRegistry.Default),
            Throws.TypeOf<FileNotFoundException>().With.Message.Contains("nope.csv"));
        Assert.That(
            () => new RunManifest("ledger.csv", [new ManifestSettlement("acme", "ledger.csv")]).ToRequest(temp.Path, SettlementFormatRegistry.Default),
            Throws.TypeOf<KeyNotFoundException>().With.Message.Contains("Known providers: canal, hashrail, orbit"));
    }

    [Test]
    public async Task An_empty_manifest_is_rejected()
    {
        using var temp = new TempDirectory();
        temp.File(RunManifest.FileName, """{ "ledger": "ledger.csv", "settlements": [] }""");

        Assert.That(() => RunManifest.ReadAsync(temp.Path), Throws.TypeOf<InvalidDataException>());
        await Task.CompletedTask;
    }
}
