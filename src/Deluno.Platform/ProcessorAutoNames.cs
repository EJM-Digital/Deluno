using System.Text.RegularExpressions;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Platform;

/// <summary>
/// #1144: a connection added before processors published their own name is called "Processor on RIG". When the
/// processor next answers its capabilities with a top-level product name, the connection is renamed to
/// "&lt;product&gt; on RIG". Only the generated form is touched: a name someone typed is never renamed, and a processor
/// that publishes no product leaves its name alone. Generic: nothing here knows which product it is.
/// </summary>
public static partial class ProcessorAutoNames
{
    [GeneratedRegex(@"^Processor on (?<machine>\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedName();

    /// <summary>The machine part of a still-generated "Processor on X" name; null for any other name.</summary>
    public static string? GeneratedMachine(string? name)
        => name is not null && GeneratedName().Match(name.Trim()) is { Success: true } match ? match.Groups["machine"].Value.Trim() : null;

    /// <summary>Renames <paramref name="connection"/> when its name is still generated and the processor now names itself. Returns the connection as it stands afterwards.</summary>
    public static async Task<ProcessorConnectionItem> RenameIfGeneratedAsync(
        ProcessorConnectionItem connection,
        IProcessorRepository repository,
        IProcessorConnectionService service,
        CancellationToken cancellationToken)
    {
        if (GeneratedMachine(connection.Name) is not { } machine || connection.SecretUnreadable)
        {
            return connection;
        }

        var product = await service.GetProductNameAsync(connection.SubmissionUrl, connection.AuthHeaderName, connection.Secret, cancellationToken);
        if (string.IsNullOrWhiteSpace(product) || string.Equals(product, "Processor", StringComparison.OrdinalIgnoreCase))
        {
            return connection;
        }

        var renamed = await repository.UpdateProcessorConnectionAsync(
            connection.Id,
            new UpdateProcessorConnectionRequest(null, null, null, null, null, connection.IsEnabled)
            {
                MachineName = machine,
                ProductName = product
            },
            cancellationToken);
        return renamed ?? connection;
    }
}
