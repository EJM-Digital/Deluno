using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Processing;
using Deluno.Tests.Support;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using static Deluno.Tests.Support.ReleaseNameGenerators;

namespace Deluno.Platform.Tests.Properties;

/// <summary>
/// #1271: path mapping (map then unmap is the identity, no escape outside the root, Windows and POSIX
/// separators) and the naming templates (a round trip, no illegal path characters, length limits).
/// </summary>
public sealed class PathMappingAndNamingProperties
{
    private sealed record Mapping(string Source, string Target, int Priority = 0, bool Enabled = true);

    private static string? Translate(string? path, params Mapping[] mappings)
        => PathMappingTranslator.Translate(path, mappings, m => m.Enabled, m => m.Source, m => m.Target, m => m.Priority);

    private static string Key(string? path) => PathMappingTranslator.NormalizeKey(path ?? string.Empty);

    private static Gen<string> Segment()
        => from length in Gen.Choose(1, 8)
           from chars in Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray()).ArrayOf(length)
           select new string(chars);

    /// <summary>A root as a Windows drive path, a POSIX path or a UNC share, always under a different top name per side.</summary>
    private static Gen<string> Root(string top)
        => from style in Gen.Choose(0, 2)
           from segments in Segment().ArrayOf(2)
           select style switch
           {
               0 => $"D:\\{top}\\{segments[0]}",
               1 => $"/{top}/{segments[0]}/{segments[1]}",
               _ => $"\\\\nas\\{top}\\{segments[0]}"
           };

    private sealed record Case(string Source, string Target, string[] Below, string Separator);

    private static Arbitrary<Case> Cases()
        => (from source in Root("src")
            from target in Root("dst")
            from count in Gen.Choose(0, 5) from below in Segment().ArrayOf(count)
            from separator in Gen.Elements("/", "\\")
            select new Case(source, target, below, separator)).ToArbitrary();

    private static string Under(Case c, string root, string? separator = null)
        => c.Below.Length == 0 ? root : root + (separator ?? c.Separator) + string.Join(separator ?? c.Separator, c.Below);

    [Fact]
    public void Mapping_a_path_and_mapping_the_result_back_gives_the_path_you_started_with()
        => Prop.ForAll(Cases(), c =>
        {
            var path = Under(c, c.Source);
            var there = Translate(path, new Mapping(c.Source, c.Target));
            var back = Translate(there, new Mapping(c.Target, c.Source));
            return string.Equals(Key(back), Key(path), StringComparison.OrdinalIgnoreCase);
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_mapped_path_stays_inside_the_target_root()
        => Prop.ForAll(Cases(), c =>
        {
            var result = Translate(Under(c, c.Source), new Mapping(c.Source, c.Target));
            return Key(result).StartsWith(Key(c.Target), StringComparison.OrdinalIgnoreCase);
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void Slash_and_backslash_in_the_reported_path_give_the_same_mapped_path()
        => Prop.ForAll(Cases(), c =>
        {
            var forward = Translate(Under(c, c.Source, "/"), new Mapping(c.Source, c.Target));
            var back = Translate(Under(c, c.Source, "\\"), new Mapping(c.Source, c.Target));
            return Key(forward) == Key(back);
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_trailing_separator_on_either_root_or_the_path_changes_nothing()
        => Prop.ForAll(Cases(), c =>
        {
            var plain = Translate(Under(c, c.Source), new Mapping(c.Source, c.Target));
            var slashed = Translate(Under(c, c.Source) + "/", new Mapping(c.Source + "\\", c.Target + "/"));
            return Key(plain) == Key(slashed);
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_path_that_is_under_no_root_comes_back_unchanged_even_when_a_root_is_only_a_text_prefix_of_it()
        => Prop.ForAll(Cases(), c =>
        {
            var sibling = c.Source + "2" + c.Separator + "file";
            var elsewhere = "/somewhere/else/" + string.Join('/', c.Below);
            return Translate(sibling, new Mapping(c.Source, c.Target)) == sibling
                   && Translate(elsewhere, new Mapping(c.Source, c.Target)) == elsewhere;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void The_longest_matching_root_wins_whatever_order_the_mappings_come_in()
        => Prop.ForAll(Cases(), c =>
        {
            var broad = new Mapping(c.Source, c.Target);
            var narrow = new Mapping(c.Source + "/inner", "/special");
            var path = c.Source + "/inner/" + string.Join('/', c.Below.Append("file"));
            var first = Translate(path, broad, narrow);
            var second = Translate(path, narrow, broad);
            return Key(first) == Key(second) && Key(first).StartsWith("/special/", StringComparison.Ordinal);
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_disabled_mapping_never_applies()
        => Prop.ForAll(Cases(), c =>
        {
            var path = Under(c, c.Source);
            return Translate(path, new Mapping(c.Source, c.Target, Enabled: false)) == path;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_processor_path_mapping_and_its_reverse_are_the_identity()
        => Prop.ForAll(Cases(), c =>
        {
            var item = new ProcessorPathMappingItem("1", "p", c.Source, c.Target, true, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            var path = Under(c, c.Source);
            var there = ProcessorPathMappingTranslator.ToProcessor(path, [item]);
            var back = ProcessorPathMappingTranslator.ToDeluno(there, [item]);
            return string.Equals(Key(back), Key(path), StringComparison.OrdinalIgnoreCase);
        }).QuickCheckThrowOnFailure();

    [Property]
    public bool Translating_any_text_with_any_roots_never_throws_and_with_no_mappings_returns_the_text_unchanged(string? path, NonEmptyString source, NonEmptyString target)
    {
        _ = Translate(path, new Mapping(source.Get, target.Get));
        return Translate(path) == path;
    }

    [Fact(Skip = "DEFECT: a path under the source root that climbs out with \"..\" is mapped to a path that climbs out of the target root. \"/src/media/../../etc/passwd\" becomes \"/dst/media/../../etc/passwd\" and only the final file system decides where that is. Nothing removes or refuses \"..\". Owner to decide.")]
    public void A_mapped_path_with_dot_dot_segments_never_leaves_the_target_root()
        => Prop.ForAll(Cases(), c =>
        {
            var path = Under(c, c.Source) + "/../../outside";
            var result = Translate(path, new Mapping(c.Source, c.Target));
            return !Key(result).Split('/').Contains("..");
        }).QuickCheckThrowOnFailure();

    [Fact(Skip = "DEFECT (shrunk case): see A_mapped_path_with_dot_dot_segments_never_leaves_the_target_root.")]
    public void Climbing_out_of_the_source_root_does_not_climb_out_of_the_target_root_shrunk_case()
        => Assert.DoesNotContain("..", Key(Translate("/src/media/../../etc/passwd", new Mapping("/src/media", "/dst/media"))).Split('/'));

    // ----- naming templates -----

    private static readonly char[] IllegalOnAnySystem = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static IEnumerable<string> SegmentsOf(string folder) => folder.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    [Property]
    public bool A_rendered_segment_holds_no_character_that_is_illegal_in_a_file_name(string? title, int? year)
    {
        var rendered = NamingTemplateRenderer.RenderSegment("{Title} ({Year}) {Quality}", title, year);
        return rendered.IndexOfAny(IllegalOnAnySystem) < 0;
    }

    [Property]
    public bool A_rendered_folder_is_never_rooted_never_climbs_and_never_has_an_empty_name(string? title, int? year)
    {
        var folder = NamingTemplateRenderer.RenderFolder("{Genre}\\{Title} ({Year})", title, year, genre: title);
        var segments = SegmentsOf(folder).ToArray();
        return folder.Length > 0
               && !Path.IsPathRooted(folder)
               && segments.Length > 0
               && segments.All(s => s is not ("." or "..") && s.IndexOfAny(IllegalOnAnySystem) < 0 && s.Trim().Length > 0);
    }

    [Property]
    public bool A_title_with_separators_in_it_never_builds_a_nested_folder(string? title)
        => SegmentsOf(NamingTemplateRenderer.RenderFolder("{Title}", (title ?? string.Empty) + "/a\\b", 2001)).Count() == 1;

    [Property]
    public bool Rendering_is_deterministic_and_a_missing_year_reads_Unknown_Year(string? title)
    {
        var first = NamingTemplateRenderer.RenderSegment("{Title} ({Year})", title, null);
        var second = NamingTemplateRenderer.RenderSegment("{Title} ({Year})", title, null);
        return first == second && first.EndsWith("(Unknown Year)", StringComparison.Ordinal);
    }

    [Fact]
    public void The_title_and_year_rendered_into_a_folder_name_are_the_title_and_year_read_back_from_it()
        => Prop.ForAll(
            (from words in Words(1, 5) from year in Gen.Choose(1900, 2099) select (words, year)).ToArbitrary(),
            item =>
            {
                var title = string.Join(' ', item.words);
                var rendered = NamingTemplateRenderer.RenderSegment("{Title} ({Year})", title, item.year);
                var (readTitle, readYear) = ReleaseTitleMatch.ParseTitleAndYear(rendered);
                return readTitle == title && readYear == item.year;
            }).QuickCheckThrowOnFailure();

    [Fact(Skip = "DEFECT: the year 2100 is a year to one parser and not to another. ReleaseNameParser.InferYear accepts 1900 to 2100, while ReleaseTitleMatch.ParseTitleAndYear and FeedReleaseNames only know 19xx and 20xx, so a folder named \"Title (2100)\" reads back with no year. Harmless today; owner to decide.")]
    public void A_title_rendered_with_the_year_2100_reads_back_with_that_year_shrunk_case()
        => Assert.Equal(2100, ReleaseTitleMatch.ParseTitleAndYear(NamingTemplateRenderer.RenderSegment("{Title} ({Year})", "House", 2100)).Year);

    [Fact]
    public void A_template_with_no_tokens_renders_as_itself_and_an_unknown_token_is_kept_visible()
        => Prop.ForAll(Segment().ToArbitrary(), text =>
            NamingTemplateRenderer.RenderSegment(text, "Title", 2001) == text
            && NamingTemplateRenderer.RenderSegment("{" + text + "!}", "Title", 2001) == "{" + text + "!}")
            .QuickCheckThrowOnFailure();

    [Fact(Skip = "DEFECT: a long title makes a folder or file name longer than the 255 characters every common file system allows. The renderer applies no length limit, so a 300-character title is written as a 300-character name and the import fails at the disk. Owner to decide.")]
    public void A_rendered_segment_is_never_longer_than_255_characters()
        => Prop.ForAll(Gen.Choose(1, 600).ToArbitrary(), length =>
            NamingTemplateRenderer.RenderSegment("{Title} ({Year})", new string('a', length), 2001).Length <= 255)
            .QuickCheckThrowOnFailure();

    [Fact(Skip = "DEFECT (shrunk case): see A_rendered_segment_is_never_longer_than_255_characters.")]
    public void A_300_character_title_is_cut_to_a_name_the_disk_accepts_shrunk_case()
        => Assert.True(NamingTemplateRenderer.RenderSegment("{Title} ({Year})", new string('a', 300), 2001).Length <= 255);

    [Fact(Skip = "DEFECT: a title ending in a dot (\"Mr.\") is written as a folder name ending in a dot with the year after it, which is fine, but a template with only {Title} writes \"Mr.\" and Windows silently drops the dot, so the name Deluno records is not the name on disk. Owner to decide.")]
    public void A_rendered_segment_never_ends_in_a_dot_or_a_space()
        => Prop.ForAll(Segment().ToArbitrary(), word =>
        {
            var rendered = NamingTemplateRenderer.RenderSegment("{Title}", word + ".", 2001);
            return !rendered.EndsWith('.') && !rendered.EndsWith(' ');
        }).QuickCheckThrowOnFailure();

    [Fact(Skip = "DEFECT (shrunk case): see A_rendered_segment_never_ends_in_a_dot_or_a_space.")]
    public void A_title_ending_in_a_dot_is_not_rendered_ending_in_a_dot_shrunk_case()
        => Assert.False(NamingTemplateRenderer.RenderSegment("{Title}", "Mr.", 2001).EndsWith('.'));

    [Fact(Skip = "DEFECT: a control character in a title (a line break from a pasted description) survives into the file name on Linux, where Path.GetInvalidFileNameChars is only NUL and slash. The sanitizer's own fixed list stops at the nine printable characters. Windows rejects it at the disk. Owner to decide.")]
    public void A_rendered_segment_holds_no_control_character()
        => Prop.ForAll(Gen.Choose(1, 31).ToArbitrary(), code =>
            NamingTemplateRenderer.RenderSegment("{Title}", "A" + (char)code + "B", 2001).All(c => !char.IsControl(c)))
            .QuickCheckThrowOnFailure();

    [Fact(Skip = "DEFECT (shrunk case): see A_rendered_segment_holds_no_control_character.")]
    public void A_title_with_a_line_break_is_not_rendered_with_the_line_break_shrunk_case()
        => Assert.DoesNotContain('\n', NamingTemplateRenderer.RenderSegment("{Title}", "A\nB", 2001));
}
