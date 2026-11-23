using System.ComponentModel.DataAnnotations;

namespace SettlementRecon.Worker.Storage;

public enum StorageProvider
{
    InMemory,
    Mongo,
}

public sealed class StorageOptions : IValidatableObject
{
    public const string SectionName = "Storage";

    public StorageProvider Provider { get; set; } = StorageProvider.InMemory;

    public MongoStorageOptions Mongo { get; set; } = new();

    /// <summary>How many runs the in-memory store keeps.</summary>
    [Range(1, 10_000)]
    public int InMemoryMaxRuns { get; set; } = 50;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Provider != StorageProvider.Mongo)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(Mongo.ConnectionString))
        {
            yield return new ValidationResult("Storage:Mongo:ConnectionString is required when Storage:Provider is Mongo.", [nameof(Mongo)]);
        }

        if (string.IsNullOrWhiteSpace(Mongo.Database))
        {
            yield return new ValidationResult("Storage:Mongo:Database must not be empty.", [nameof(Mongo)]);
        }
    }
}

public sealed class MongoStorageOptions
{
    /// <summary>Never logged. Supply it through an environment variable or a secret store, not appsettings.</summary>
    public string? ConnectionString { get; set; }

    public string Database { get; set; } = "settlement_recon";

    public TimeSpan ServerSelectionTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
