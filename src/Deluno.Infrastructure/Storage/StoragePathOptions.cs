namespace Deluno.Infrastructure.Storage;

public sealed class StoragePathOptions
{
    public const string SectionName = "Storage";

    public string DataRoot { get; set; } = "data";
}

