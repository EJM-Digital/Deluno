using System.Globalization;
using System.Xml.Linq;

namespace Deluno.Downloader.Nzb.Parser;

/// <summary>
/// NZB 1.1 document model. NZB is an XML manifest pointing at the
/// Message-IDs that compose a binary post: namespace
/// <c>http://www.newzbin.com/DTD/2003/nzb</c>, one or more <c>&lt;file&gt;</c>
/// entries, each with groups + ordered <c>&lt;segment&gt;</c> references.
///
/// Production-grade differences from the spike version:
/// <list type="bullet">
///   <item><description>Parses the optional <c>&lt;meta&gt;</c> block (NZB 1.1) for
///     <c>password</c>, <c>category</c>, <c>name</c>.</description></item>
///   <item><description>Extracts <c>{{password}}</c> from filenames as a fallback
///     password source (community convention).</description></item>
///   <item><description>Deduplicates segments by message-id within a file.</description></item>
///   <item><description>Wraps <c>XmlException</c> in <see cref="InvalidDataException"/> so
///     the caller has a single error type.</description></item>
///   <item><description>Tolerant of NZB documents missing the canonical namespace.</description></item>
/// </list>
/// </summary>
public sealed record NzbDocument(
    IReadOnlyList<NzbFile> Files,
    NzbMeta Meta)
{
    private static readonly XNamespace Ns = "http://www.newzbin.com/DTD/2003/nzb";

    public static NzbDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static NzbDocument Load(Stream stream)
    {
        XDocument doc;
        try { doc = XDocument.Load(stream); }
        catch (System.Xml.XmlException ex) { throw new InvalidDataException("Malformed NZB XML.", ex); }
        return Parse(doc);
    }

    public static NzbDocument Parse(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException ex) { throw new InvalidDataException("Malformed NZB XML.", ex); }
        return Parse(doc);
    }

    public static NzbDocument Parse(XDocument doc)
    {
        if (doc.Root is null)
            throw new InvalidDataException("NZB document has no root element.");

        // Tolerate documents with or without the canonical namespace.
        var ns = doc.Root.GetDefaultNamespace();
        if (string.IsNullOrEmpty(ns.NamespaceName))
            ns = XNamespace.None;

        // Head/meta block (NZB 1.1).
        string? password = null;
        string? category = null;
        string? name = null;
        var head = doc.Root.Element(ns + "head");
        if (head is not null)
        {
            foreach (var meta in head.Elements(ns + "meta"))
            {
                var type = (string?)meta.Attribute("type");
                var value = meta.Value?.Trim();
                if (string.IsNullOrEmpty(value)) continue;
                switch (type?.ToLowerInvariant())
                {
                    case "password": password = value; break;
                    case "category": category = value; break;
                    case "name":     name     = value; break;
                }
            }
        }

        // Files.
        var files = new List<NzbFile>();
        foreach (var fileEl in doc.Root.Elements(ns + "file"))
        {
            files.Add(ParseFile(fileEl, ns));
        }

        // Fallback: extract password from filename of the first file
        // ({{password}} convention) if not in meta.
        if (string.IsNullOrEmpty(password) && files.Count > 0)
            password = ExtractPasswordFromFilename(files[0].FileName ?? files[0].Subject);

        return new NzbDocument(files, new NzbMeta(password, category, name));
    }

    private static NzbFile ParseFile(XElement fileEl, XNamespace ns)
    {
        var poster = (string?)fileEl.Attribute("poster") ?? string.Empty;
        var subject = (string?)fileEl.Attribute("subject") ?? string.Empty;
        var dateAttr = (string?)fileEl.Attribute("date");
        DateTimeOffset? date = null;
        if (long.TryParse(dateAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            date = DateTimeOffset.FromUnixTimeSeconds(unix);

        var groups = fileEl.Element(ns + "groups")?.Elements(ns + "group")
            .Select(g => g.Value.Trim())
            .Where(g => g.Length > 0)
            .ToList() ?? new List<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var segments = new List<NzbSegment>();
        var segmentsEl = fileEl.Element(ns + "segments");
        if (segmentsEl is not null)
        {
            foreach (var seg in segmentsEl.Elements(ns + "segment"))
            {
                var bytes = (long?)seg.Attribute("bytes") ?? 0;
                var number = (int?)seg.Attribute("number") ?? 0;
                var msgId = seg.Value.Trim();
                if (msgId.Length == 0) continue;
                if (!seen.Add(msgId)) continue;  // dedupe by message-id
                segments.Add(new NzbSegment(number, bytes, msgId));
            }
        }
        segments.Sort((a, b) => a.Number.CompareTo(b.Number));

        return new NzbFile(poster, subject, date, groups, segments);
    }

    /// <summary>
    /// Community convention: passwords embedded in filenames as
    /// <c>{{password}}</c>. Returns null if not found.
    /// </summary>
    private static string? ExtractPasswordFromFilename(string source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        var open = source.IndexOf("{{", StringComparison.Ordinal);
        if (open < 0) return null;
        var close = source.IndexOf("}}", open + 2, StringComparison.Ordinal);
        if (close < 0) return null;
        var password = source.Substring(open + 2, close - open - 2).Trim();
        return string.IsNullOrEmpty(password) ? null : password;
    }

    public long TotalBytes => Files.Sum(f => f.TotalBytes);
    public IEnumerable<NzbFile> Par2Files => Files.Where(f => f.IsPar2);
    public IEnumerable<NzbFile> PayloadFiles => Files.Where(f => !f.IsPar2);
}

public sealed record NzbFile(
    string Poster,
    string Subject,
    DateTimeOffset? Date,
    IReadOnlyList<string> Groups,
    IReadOnlyList<NzbSegment> Segments)
{
    public long TotalBytes => Segments.Sum(s => s.Bytes);

    public string? FileName
    {
        get
        {
            if (string.IsNullOrEmpty(Subject)) return null;
            var first = Subject.IndexOf('"');
            if (first < 0) return null;
            var second = Subject.IndexOf('"', first + 1);
            if (second < 0) return null;
            var name = Subject.Substring(first + 1, second - first - 1).Trim();
            return name.Length == 0 ? null : name;
        }
    }

    public bool IsPar2
    {
        get
        {
            var name = FileName ?? Subject;
            return name.Contains(".par2", StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed record NzbSegment(int Number, long Bytes, string MessageId);

/// <summary>Head/meta block on the NZB document.</summary>
public sealed record NzbMeta(string? Password, string? Category, string? Name);
