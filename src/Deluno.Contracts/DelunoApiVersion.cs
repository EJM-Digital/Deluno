namespace Deluno.Contracts;

/// <summary>
/// The one place the API's current version is defined. The host's version
/// alias middleware, the integration manifest endpoint, and the OpenAPI
/// document all read this instead of each carrying their own literal.
/// </summary>
public static class DelunoApiVersion
{
    public const string Current = "v1";
}
