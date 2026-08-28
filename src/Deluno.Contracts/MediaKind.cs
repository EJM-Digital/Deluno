using Deluno.Contracts;
namespace Deluno.Contracts;

/// <summary>
/// Which of the two catalogues a thing belongs to.
///
/// <para>This lived in <c>Deluno.Media</c>, which is downstream of contracts, so
/// nothing in <c>Deluno.Contracts</c> could say "movies only" or "TV only". That
/// is the whole subject of #324: the filter fields, the sorts and the poster
/// options are declared per kind, and the declaration has to sit beside the
/// contracts the API speaks — not in a module the contracts cannot see.</para>
///
/// <para>The rest of the codebase keeps saying <c>MediaKind</c>; only the
/// namespace moved.</para>
/// </summary>
public enum MediaKind
{
    Movie,
    Series
}
