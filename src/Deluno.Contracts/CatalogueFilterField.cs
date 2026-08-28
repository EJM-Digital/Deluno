namespace Deluno.Contracts;

/// <summary>
/// Which half of a title a filter asks about, and therefore which heading it
/// appears under.
///
/// <para>The grouping is not decoration. Radarr prints, in its own Custom
/// Filters dialog, that its filters are "available only for the properties of a
/// movie, they are not available for properties of the file(s) you may have" —
/// so <see cref="File"/> and <see cref="Decision"/> are the two groups nothing
/// else in this space has at all, and they are worth naming on screen.</para>
/// </summary>
public enum CatalogueFilterGroup
{
    /// <summary>What the title is: name, year, genre, rating, network, studio.</summary>
    Title,

    /// <summary>What the copy you hold is: quality, size, codec, group, path.</summary>
    File,

    /// <summary>When something happened: added, released, aired, last searched.</summary>
    Time,

    /// <summary>What Deluno concluded and why. Nothing else asks these.</summary>
    Decision
}

/// <summary>
/// What kind of value a field takes, which decides the editor the browser draws
/// and the way the value binds in SQL.
///
/// <para>Declared once here rather than inferred from the column, because the
/// column cannot tell you that <c>file_size_bytes</c> is entered in gigabytes or
/// that <c>current_quality</c> should offer the quality ladder rather than a
/// free-text box.</para>
/// </summary>
public enum CatalogueFilterValueKind
{
    /// <summary>Free text, matched case-insensitively.</summary>
    Text,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A number with a fractional part.</summary>
    Decimal,

    /// <summary>A four-digit year.</summary>
    Year,

    /// <summary>A duration entered in minutes.</summary>
    Minutes,

    /// <summary>Entered in gigabytes, stored in bytes. The binder converts.</summary>
    Gigabytes,

    /// <summary>A metadata score out of ten.</summary>
    Rating,

    /// <summary>An ISO instant, and the only kind that takes relative operators.</summary>
    Date,

    /// <summary>Yes or no.</summary>
    Boolean,

    /// <summary>
    /// A tier from the quality ladder. The options are not listed here: they come
    /// from <c>/api/quality-model</c>, which is the same source Library Profiles
    /// and Size Rules read, so a filter can never offer a tier the ladder does
    /// not have.
    /// </summary>
    QualityTier,

    /// <summary>
    /// A genre from the catalogue itself, served by <c>/api/{kind}/genres</c>.
    /// One pass over one column, so the list is the whole library rather than
    /// whatever the current page happens to be tagged with.
    /// </summary>
    Genre,

    /// <summary>A closed list declared on the field itself.</summary>
    Enum
}

/// <summary>
/// How a field is compared to the values beside it.
///
/// <para>Free-form <em>combination</em> over a closed, typed, server-known
/// vocabulary — which is the resolution DESIGN-004 reached to the argument
/// DESIGN-003 settled. The rule engine deleted in #302 could name a field
/// nothing set and so match zero rows forever without saying so. This cannot:
/// every field resolves to one real column, every operator to one expression,
/// and an id the registry does not know is a 400 rather than a silent
/// nothing.</para>
/// </summary>
public enum CatalogueFilterOperator
{
    /// <summary>Any one of the values. The list is an OR.</summary>
    Includes,

    /// <summary>None of the values.</summary>
    Excludes,

    /// <summary>Every one of the values is present, which is what picking two genres means.</summary>
    IncludesAll,

    Is,
    IsNot,

    /// <summary>At or above. Blank means no limit, never zero.</summary>
    AtLeast,

    /// <summary>At or below.</summary>
    AtMost,

    Contains,
    DoesNotContain,
    StartsWith,
    EndsWith,

    /// <summary>Earlier than an absolute date.</summary>
    Before,

    /// <summary>Later than an absolute date.</summary>
    After,

    /// <summary>
    /// Within the last N days — relative, so a saved view built on it stays true
    /// next month. Radarr's date filters take absolute dates only, which is why
    /// "added recently" there is a filter you rewrite every month.
    /// </summary>
    WithinLastDays,

    /// <summary>Longer ago than N days, or not at all.</summary>
    MoreThanDaysAgo,

    /// <summary>Has any value.</summary>
    IsSet,

    /// <summary>Has no value.</summary>
    IsNotSet
}

/// <summary>
/// Where the column lives, which decides whether asking the question costs a
/// join.
/// </summary>
public enum CatalogueFilterSource
{
    /// <summary>On the entries table. Free — the page already reads it.</summary>
    Entry,

    /// <summary>
    /// On the one wanted-state row the page speaks for.
    ///
    /// <para>Read through <c>ws</c>, never an <c>EXISTS</c> over all of them. A
    /// title held in two libraries has two files, and matching on one library's
    /// while displaying another's is precisely the drift
    /// <see cref="Deluno.Contracts"/>' wanted-state pick was introduced to
    /// end.</para>
    /// </summary>
    WantedState
}

/// <summary>
/// One question a shelf can be asked.
///
/// <para>This is the whole answer to #324's third question — whether a filter set
/// is data rather than code. It is data, and that is not the generic rule engine
/// #302 deleted: this record is a <em>declaration of a real column</em>, written
/// by somebody who found the column, and the browser renders the list the server
/// serves rather than keeping its own copy. Adding a filter is a row here plus,
/// where the column does not exist yet, a migration.</para>
/// </summary>
/// <param name="Id">Stable, and what travels on the query string.</param>
/// <param name="Column">
/// The SQL expression, with <c>{alias}</c> standing in for the entries alias.
/// Never interpolated from caller input — only from this table.
/// </param>
/// <param name="Options">
/// The closed list for <see cref="CatalogueFilterValueKind.Enum"/>. Null for
/// every other kind; quality tiers and genres are served from their own
/// endpoints so a stale copy cannot exist here.
/// </param>
public sealed record CatalogueFilterField(
    string Id,
    string Label,
    string Hint,
    CatalogueFilterGroup Group,
    CatalogueFilterValueKind ValueKind,
    CatalogueFilterSource Source,
    string Column,
    IReadOnlyList<string>? Options = null)
{
    /// <summary>
    /// Which operators this field accepts, derived from its value kind rather
    /// than declared per field.
    ///
    /// <para>Derived on purpose: a per-field list is thirty chances to give one
    /// text field a comparison the others do not have, and the difference would
    /// only ever show up as a control that is missing on one row.</para>
    /// </summary>
    public IReadOnlyList<CatalogueFilterOperator> Operators => OperatorsFor(ValueKind);

    public static IReadOnlyList<CatalogueFilterOperator> OperatorsFor(CatalogueFilterValueKind kind)
        => kind switch
        {
            CatalogueFilterValueKind.Text =>
            [
                CatalogueFilterOperator.Contains,
                CatalogueFilterOperator.DoesNotContain,
                CatalogueFilterOperator.Is,
                CatalogueFilterOperator.IsNot,
                CatalogueFilterOperator.StartsWith,
                CatalogueFilterOperator.EndsWith,
                CatalogueFilterOperator.IsSet,
                CatalogueFilterOperator.IsNotSet
            ],
            CatalogueFilterValueKind.Genre =>
            [
                // Every genre picked must be present, first, because that is
                // what a reader means by picking two — and it is what the
                // control did before this registry existed.
                CatalogueFilterOperator.IncludesAll,
                CatalogueFilterOperator.Includes,
                CatalogueFilterOperator.Excludes
            ],
            CatalogueFilterValueKind.QualityTier or CatalogueFilterValueKind.Enum =>
            [
                CatalogueFilterOperator.Includes,
                CatalogueFilterOperator.Excludes,
                CatalogueFilterOperator.IsSet,
                CatalogueFilterOperator.IsNotSet
            ],
            CatalogueFilterValueKind.Boolean => [CatalogueFilterOperator.Is],
            CatalogueFilterValueKind.Date =>
            [
                CatalogueFilterOperator.WithinLastDays,
                CatalogueFilterOperator.MoreThanDaysAgo,
                CatalogueFilterOperator.After,
                CatalogueFilterOperator.Before,
                CatalogueFilterOperator.IsSet,
                CatalogueFilterOperator.IsNotSet
            ],
            _ =>
            [
                CatalogueFilterOperator.AtLeast,
                CatalogueFilterOperator.AtMost,
                CatalogueFilterOperator.Is,
                CatalogueFilterOperator.IsNot,
                CatalogueFilterOperator.IsSet,
                CatalogueFilterOperator.IsNotSet
            ]
        };
}

/// <summary>
/// The short token an operator travels as, and back again.
///
/// <para>Words rather than symbols because these appear in a URL somebody may
/// read, bookmark or share — the same reason the narrowing has never been a
/// base64 blob.</para>
/// </summary>
public static class CatalogueFilterOperators
{
    private static readonly IReadOnlyDictionary<CatalogueFilterOperator, string> Tokens =
        new Dictionary<CatalogueFilterOperator, string>
        {
            [CatalogueFilterOperator.Includes] = "in",
            [CatalogueFilterOperator.Excludes] = "notin",
            [CatalogueFilterOperator.IncludesAll] = "all",
            [CatalogueFilterOperator.Is] = "is",
            [CatalogueFilterOperator.IsNot] = "isnot",
            [CatalogueFilterOperator.AtLeast] = "min",
            [CatalogueFilterOperator.AtMost] = "max",
            [CatalogueFilterOperator.Contains] = "has",
            [CatalogueFilterOperator.DoesNotContain] = "nothas",
            [CatalogueFilterOperator.StartsWith] = "starts",
            [CatalogueFilterOperator.EndsWith] = "ends",
            [CatalogueFilterOperator.Before] = "before",
            [CatalogueFilterOperator.After] = "after",
            [CatalogueFilterOperator.WithinLastDays] = "within",
            [CatalogueFilterOperator.MoreThanDaysAgo] = "beyond",
            [CatalogueFilterOperator.IsSet] = "set",
            [CatalogueFilterOperator.IsNotSet] = "unset"
        };

    private static readonly IReadOnlyDictionary<string, CatalogueFilterOperator> ByToken =
        Tokens.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    public static string Token(CatalogueFilterOperator op) => Tokens[op];

    public static bool TryParse(string? token, out CatalogueFilterOperator op)
    {
        op = default;
        return !string.IsNullOrWhiteSpace(token) && ByToken.TryGetValue(token.Trim(), out op);
    }

    /// <summary>Whether the operator needs values beside it at all.</summary>
    public static bool TakesValues(CatalogueFilterOperator op)
        => op is not (CatalogueFilterOperator.IsSet or CatalogueFilterOperator.IsNotSet);
}
