using System.Text.Json;

namespace Deluno.Contracts;

/// <summary>
/// One question, asked of one field.
/// </summary>
/// <param name="FieldId">An id from <see cref="CatalogueFilterFields"/> for this media kind.</param>
/// <param name="Values">
/// What the field is compared against. A list, because
/// <see cref="CatalogueFilterOperator.Includes"/> and its siblings are an OR
/// over it. Empty for the operators that take no value.
/// </param>
public sealed record CatalogueFilterCondition(
    string FieldId,
    CatalogueFilterOperator Operator,
    IReadOnlyList<string> Values)
{
    /// <summary>
    /// The form this travels in on a query string: <c>field:operator:a|b|c</c>.
    ///
    /// <para>Flat and readable rather than a JSON blob, because these travel in a
    /// URL people bookmark, share and read. Split on the first two colons only,
    /// so a Windows path in a value survives.</para>
    /// </summary>
    public string Encode()
        => Values.Count == 0
            ? $"{FieldId}:{CatalogueFilterOperators.Token(Operator)}"
            : $"{FieldId}:{CatalogueFilterOperators.Token(Operator)}:{string.Join('|', Values)}";
}

/// <summary>
/// The narrowing a catalogue page can be asked for beyond its status and its
/// library.
///
/// <para><b>Named fields, and now a free combination of them.</b> This was nine
/// fixed properties — quality, genres, and four ranges — which was the right
/// shape for six filters and the wrong one for the sixty this has to reach.
/// Radarr offers 33 filter fields; the standing check in the north star is that
/// where a tool offers N of something, Deluno offers all N and then more, and
/// thirty-three hand-written properties on a record is the shape that becomes
/// unreadable and then wrong.</para>
///
/// <para><b>This is still not the rule engine #302 deleted.</b> That one had a
/// 45-value <c>FilterField</c> union in the browser, and two of its values named
/// states nothing ever set, so those branches matched zero rows forever and
/// nothing said so. The difference is where the vocabulary lives and what
/// happens to a word outside it: every field here is one real stored column,
/// declared server-side in <see cref="CatalogueFilterFields"/>, served to the
/// browser rather than copied there, and an id this media kind does not have is
/// a 400 — never a silently dropped condition. A closed vocabulary with a free
/// combination cannot ask an unanswerable question; an open vocabulary can, and
/// did.</para>
///
/// <para><b>Everything narrows.</b> Conditions combine with AND, and the values
/// inside one are an OR.</para>
/// </summary>
public sealed record CatalogueFilters(IReadOnlyList<CatalogueFilterCondition>? Conditions = null)
{
    public static readonly CatalogueFilters None = new();

    /// <summary>
    /// Whether anything is actually being asked for. A page with no filters must
    /// run exactly the query it ran before this existed — the same rule the
    /// subtitle rollup follows, and the reason a feature nobody uses costs
    /// nothing.
    /// </summary>
    public bool IsEmpty => Conditions is null || Conditions.Count == 0;

    /// <summary>
    /// Whether answering this needs the wanted-state row joined on.
    ///
    /// <para>Asked precisely rather than "are there any filters at all", so
    /// narrowing by year still costs a page nothing extra. The list page already
    /// joins it for the columns it displays; the facets query does not, and that
    /// is where the difference is paid.</para>
    /// </summary>
    public bool NeedsWantedState(MediaKind kind)
        => Conditions is not null
           && Conditions.Any(condition =>
               CatalogueFilterFields.Find(kind, condition.FieldId) is { Source: CatalogueFilterSource.WantedState });

    /// <summary>
    /// Reads the repeated <c>f=</c> parameters a catalogue request carries.
    ///
    /// <para>Anything the registry does not know for this kind lands in
    /// <paramref name="errors"/> so the endpoint can refuse the request. Dropping
    /// it instead would hand back a shelf that looks narrowed and is not.</para>
    /// </summary>
    public static CatalogueFilters Parse(MediaKind kind, IEnumerable<string?>? raw, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        var conditions = new List<CatalogueFilterCondition>();

        foreach (var entry in raw ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var parts = entry.Split(':', 3);
            if (parts.Length < 2)
            {
                problems.Add($"“{entry}” is not a filter. Write it as field:operator:value.");
                continue;
            }

            var field = CatalogueFilterFields.Find(kind, parts[0]);
            if (field is null)
            {
                problems.Add($"There is no “{parts[0]}” to filter {(kind == MediaKind.Movie ? "movies" : "TV")} by.");
                continue;
            }

            if (!CatalogueFilterOperators.TryParse(parts[1], out var op) || !field.Operators.Contains(op))
            {
                problems.Add($"“{field.Label}” cannot be compared with “{parts[1]}”.");
                continue;
            }

            var values = parts.Length == 3
                ? parts[2].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];

            if (CatalogueFilterOperators.TakesValues(op) && values.Length == 0)
            {
                problems.Add($"“{field.Label}” needs a value.");
                continue;
            }

            conditions.Add(new CatalogueFilterCondition(field.Id, op, values));
        }

        errors = problems;
        return conditions.Count == 0 ? None : new CatalogueFilters(conditions);
    }

    /// <summary>
    /// Reads the condition array stored by a saved library view.
    ///
    /// <para>Saved views predate the worker's automation scope and have held
    /// more than one shape over their lifetime. Automation cannot afford the
    /// browser parser's forgiving migration behaviour: an unreadable rule must
    /// disable that scope rather than become an unfiltered library search.
    /// Every field and operator is therefore checked against the same registry
    /// used by the catalogue endpoints.</para>
    /// </summary>
    public static bool TryParseJson(MediaKind kind, string? raw, out CatalogueFilters filters)
    {
        filters = None;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var conditions = new List<CatalogueFilterCondition>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !TryGetStringProperty(element, "field", out var fieldId) ||
                    !TryGetOperator(element, out var op) ||
                    !TryGetValues(element, out var values))
                {
                    return false;
                }

                var field = CatalogueFilterFields.Find(kind, fieldId);
                if (field is null || !field.Operators.Contains(op))
                {
                    return false;
                }

                if (CatalogueFilterOperators.TakesValues(op)
                    ? values.Count == 0 || values.Any(string.IsNullOrWhiteSpace)
                    : values.Count != 0)
                {
                    return false;
                }

                conditions.Add(new CatalogueFilterCondition(field.Id, op, values));
            }

            filters = conditions.Count == 0 ? None : new CatalogueFilters(conditions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetStringProperty(JsonElement element, string name, out string value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString()?.Trim() ?? string.Empty;
                return value.Length > 0;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetOperator(JsonElement element, out CatalogueFilterOperator op)
    {
        op = default;
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, "operator", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var raw = property.Value.GetString();
                if (CatalogueFilterOperators.TryParse(raw, out op))
                {
                    return true;
                }

                return Enum.TryParse(raw, ignoreCase: true, out op) && Enum.IsDefined(op);
            }

            if (property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt32(out var numeric) &&
                Enum.IsDefined(typeof(CatalogueFilterOperator), numeric))
            {
                op = (CatalogueFilterOperator)numeric;
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryGetValues(JsonElement element, out IReadOnlyList<string> values)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, "values", StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var parsed = new List<string>();
            foreach (var value in property.Value.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    values = [];
                    return false;
                }

                parsed.Add(value.GetString() ?? string.Empty);
            }

            values = parsed;
            return true;
        }

        values = [];
        return false;
    }

    /// <summary>
    /// Everything a catalogue request can say about narrowing, in one place: the
    /// repeated <c>f=</c> conditions and the flat parameters this shipped with.
    ///
    /// <para>Written here rather than in each endpoint because the two
    /// catalogues would otherwise translate the same query string twice, and the
    /// pair would drift the first time a field was added to one of them.</para>
    ///
    /// <para>Returns false when a condition names something this media kind
    /// cannot be asked — which the endpoint turns into a 400. A shelf that
    /// looks narrowed and is not is how somebody loses half their library and
    /// concludes Deluno has.</para>
    /// </summary>
    public static bool TryBuild(
        MediaKind kind,
        IEnumerable<string?>? conditions,
        string? quality,
        string? genre,
        double? minSizeGb,
        double? maxSizeGb,
        int? minYear,
        int? maxYear,
        int? minRuntime,
        int? maxRuntime,
        double? minRating,
        out CatalogueFilters filters,
        out IReadOnlyList<string> errors)
    {
        var parsed = Parse(kind, conditions, out errors);
        if (errors.Count > 0)
        {
            filters = None;
            return false;
        }

        var legacy = FromLegacyParameters(
            quality, genre, minSizeGb, maxSizeGb, minYear, maxYear, minRuntime, maxRuntime, minRating);

        if (legacy.Count == 0)
        {
            filters = parsed;
            return true;
        }

        filters = new CatalogueFilters([.. parsed.Conditions ?? [], .. legacy]);
        return true;
    }

    /// <summary>
    /// The flat query parameters this shipped with — <c>quality</c>,
    /// <c>genre</c>, <c>minSizeGb</c> and the rest — translated into conditions.
    ///
    /// <para>Kept because URLs outlive deploys and saved views hold them, and
    /// written here rather than in each endpoint so the translation exists once.
    /// New callers send <c>f=</c>.</para>
    /// </summary>
    public static IReadOnlyList<CatalogueFilterCondition> FromLegacyParameters(
        string? quality,
        string? genre,
        double? minSizeGb,
        double? maxSizeGb,
        int? minYear,
        int? maxYear,
        int? minRuntime,
        int? maxRuntime,
        double? minRating)
    {
        var conditions = new List<CatalogueFilterCondition>();

        if (ParseList(quality) is { Count: > 0 } qualities)
        {
            conditions.Add(new CatalogueFilterCondition("quality", CatalogueFilterOperator.Includes, qualities));
        }

        if (ParseList(genre) is { Count: > 0 } genres)
        {
            conditions.Add(new CatalogueFilterCondition("genre", CatalogueFilterOperator.IncludesAll, genres));
        }

        Add("size", CatalogueFilterOperator.AtLeast, minSizeGb);
        Add("size", CatalogueFilterOperator.AtMost, maxSizeGb);
        Add("year", CatalogueFilterOperator.AtLeast, minYear);
        Add("year", CatalogueFilterOperator.AtMost, maxYear);
        Add("runtime", CatalogueFilterOperator.AtLeast, minRuntime);
        Add("runtime", CatalogueFilterOperator.AtMost, maxRuntime);
        Add("rating", CatalogueFilterOperator.AtLeast, minRating);

        return conditions;

        void Add(string fieldId, CatalogueFilterOperator op, IFormattable? value)
        {
            if (value is not null)
            {
                conditions.Add(new CatalogueFilterCondition(
                    fieldId,
                    op,
                    [value.ToString(null, System.Globalization.CultureInfo.InvariantCulture)]));
            }
        }
    }

    /// <summary>
    /// Reads the comma-separated form the legacy query string carries, dropping
    /// blanks so a trailing comma is not a filter for the empty string.
    /// </summary>
    public static IReadOnlyList<string>? ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return parts.Length == 0 ? null : parts;
    }

    /// <summary>
    /// Builds a filter set straight from conditions, for tests and for the two
    /// endpoints' legacy path. Empty in, <see cref="None"/> out.
    /// </summary>
    public static CatalogueFilters Of(params CatalogueFilterCondition[] conditions)
        => conditions.Length == 0 ? None : new CatalogueFilters(conditions);

    /// <summary>
    /// The same set with one more question on it. Conditions combine with AND,
    /// so this is what "narrow further" means.
    /// </summary>
    public CatalogueFilters And(string fieldId, CatalogueFilterOperator op, params string[] values)
        => new([.. Conditions ?? [], new CatalogueFilterCondition(fieldId, op, values)]);

    /// <summary>One condition, spelled the way a test reads best.</summary>
    public static CatalogueFilterCondition Where(string fieldId, CatalogueFilterOperator op, params string[] values)
        => new(fieldId, op, values);
}
