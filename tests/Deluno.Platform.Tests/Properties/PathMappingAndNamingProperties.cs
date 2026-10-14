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

    [Fact]
    public void A_title_rendered_with_the_year_2100_reads_back_with_that_year_shrunk_case()
        => Assert.Equal(2100, ReleaseTitleMatch.ParseTitleAndYear(NamingTemplateRenderer.RenderSegment("{Title} ({Year})", "House", 2100)).Year);

    [Fact]
    public void A_template_with_no_tokens_renders_as_itself_and_an_unknown_token_is_kept_visible()
        => Prop.ForAll(Segment().ToArbitrary(), text =>
            NamingTemplateRenderer.RenderSegment(text, "Title", 2001) == text
            && NamingTemplateRenderer.RenderSegment("{" + text + "!}", "Title", 2001) == "{" + text + "!}")
            .QuickCheckThrowOnFailure();

    private static int Bytes(string text) => System.Text.Encoding.UTF8.GetByteCount(text);

    /// <summary>Plain letters, accented letters, CJK and emoji (a surrogate pair), so a cut can land inside a character.</summary>
    private static Gen<string> LongText()
        => from length in Gen.Choose(1, 400)
           from parts in Gen.Elements("a", "b", "c", "X", " ", "é", "ü", "中", "文", "🎬").ArrayOf(length)
           select string.Concat(parts);

    [Fact]
    public void A_rendered_segment_is_never_longer_than_255_bytes_and_keeps_its_year()
        => Prop.ForAll(LongText().ToArbitrary(), text =>
        {
            var rendered = NamingTemplateRenderer.RenderSegment("{Title} ({Year}) {Quality}", text, 2001, decision: new NamingDecision(Quality: "WEBDL-1080p"));
            return Bytes(rendered) <= 255 && rendered.EndsWith("(2001) WEBDL-1080p", StringComparison.Ordinal) && !rendered.Contains('�');
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_300_character_title_is_cut_to_a_name_the_disk_accepts_shrunk_case()
    {
        var rendered = NamingTemplateRenderer.RenderSegment("{Title} ({Year})", new string('a', 300), 2001);
        Assert.True(Bytes(rendered) <= 255);
        Assert.EndsWith(" (2001)", rendered);
    }

    [Fact]
    public void A_title_of_two_byte_characters_is_cut_by_bytes_not_by_characters()
    {
        var rendered = NamingTemplateRenderer.RenderSegment("{Title} ({Year})", new string('é', 200), 2001);
        Assert.True(Bytes(rendered) <= 255);
        Assert.EndsWith(" (2001)", rendered);
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        for (var pairs = 40; pairs < 80; pairs++)
        {
            var rendered = NamingTemplateRenderer.RenderSegment("a{Title}", string.Concat(Enumerable.Repeat("🎬", pairs)), 2001);
            Assert.True(Bytes(rendered) <= 255);
            Assert.DoesNotContain('�', rendered);
            Assert.Equal(0, rendered.Count(char.IsSurrogate) % 2);
        }
    }

    [Fact]
    public void A_file_name_keeps_its_extension_when_the_title_is_cut()
    {
        var rendered = NamingTemplateRenderer.RenderSegment("{Title} ({Year})", new string('a', 400), 2001, reserveBytes: Bytes(".mkv"));
        Assert.True(Bytes(rendered + ".mkv") <= 255);
        Assert.EndsWith(" (2001)", rendered);
    }

    [Fact]
    public void A_template_without_the_title_token_is_still_cut_and_keeps_a_trailing_year()
    {
        var rendered = NamingTemplateRenderer.RenderSegment(new string('b', 300) + " ({Year})", null, 2001);
        Assert.True(Bytes(rendered) <= 255);
        Assert.EndsWith(" (2001)", rendered);
    }

    [Fact]
    public void A_rendered_folder_is_cut_per_segment()
    {
        var folder = NamingTemplateRenderer.RenderFolder("{Genre}\\{Title} ({Year})", new string('a', 400), 2001, genre: new string('g', 400));
        var segments = SegmentsOf(folder).ToArray();
        Assert.Equal(2, segments.Length);
        Assert.All(segments, segment => Assert.True(Bytes(segment) <= 255));
        Assert.EndsWith(" (2001)", segments[1]);
    }

    [Fact]
    public void A_rendered_segment_never_ends_in_a_dot_or_a_space()
        => Prop.ForAll(Segment().ToArbitrary(), word =>
        {
            var rendered = NamingTemplateRenderer.RenderSegment("{Title}", word + ". .  ", 2001);
            return rendered.Length > 0 && !rendered.EndsWith('.') && !rendered.EndsWith(' ');
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_title_ending_in_a_dot_is_not_rendered_ending_in_a_dot_shrunk_case()
    {
        Assert.Equal("Mr", NamingTemplateRenderer.RenderSegment("{Title}", "Mr.", 2001));
        Assert.Equal("Mr. (2001)", NamingTemplateRenderer.RenderSegment("{Title} ({Year})", "Mr.", 2001));
        Assert.Equal("Mr", Path.GetFileName(NamingTemplateRenderer.RenderFolder("{Title}", "Mr.", 2001)));
    }

    /// <summary>What a title became before #1308: illegal characters to a dash, outer white space trimmed.</summary>
    private static string OldTitle(string title)
        => new string(title.Select(c => "<>|?*\"/\\".Contains(c) ? '-' : c).ToArray()).Trim();

    [Fact]
    public void A_name_that_was_already_fine_renders_exactly_as_it_did_before()
        => Prop.ForAll(
            (from length in Gen.Choose(1, 300)
             from parts in Gen.Elements("a", "B", "7", " ", ".", "-", "_", "&", "'", "<", "|", "?", "\u00e9", "\u4e2d").ArrayOf(length)
             select string.Concat(parts)).ToArbitrary(),
            title =>
            {
                foreach (var template in new[] { "{Title}", "{Title} ({Year})", "{Title} ({Year}) {Quality}" })
                {
                    var oldTitle = OldTitle(title);
                    var old = template.Replace("{Title}", oldTitle).Replace("{Year}", "2001").Replace("{Quality}", "WEBDL-1080p").Trim();
                    if (oldTitle.Length == 0 || old.EndsWith('.') || old.EndsWith(' ') || old.Any(char.IsControl) || Bytes(old) > 255)
                    {
                        continue;
                    }

                    var rendered = NamingTemplateRenderer.RenderSegment(template, title, 2001, decision: new NamingDecision(Quality: "WEBDL-1080p"));
                    if (rendered != old)
                    {
                        return false;
                    }
                }

                return true;
            }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_dot_inside_a_name_is_kept_and_only_the_end_of_the_whole_name_is_trimmed()
    {
        Assert.Equal("Mr. Smith (2001)", NamingTemplateRenderer.RenderSegment("{Title} ({Year})", "Mr. Smith", 2001));
        Assert.Equal("Mr", NamingTemplateRenderer.RenderSegment("{Title}", "Mr.", 2001, reserveBytes: 4) + "");
        Assert.DoesNotContain("..", NamingTemplateRenderer.RenderSegment("{Title}", "Mr.", 2001) + ".mkv");
    }

    [Fact]
    public void A_name_made_only_of_dots_and_spaces_does_not_come_out_empty()
        => Assert.Equal("Untitled", NamingTemplateRenderer.RenderFolder("{Title}", ". . .", 2001));

    [Fact]
    public void A_rendered_segment_holds_no_control_character()
        => Prop.ForAll(Gen.Elements(Enumerable.Range(0, 32).Concat(Enumerable.Range(0x7f, 0x21)).ToArray()).ToArbitrary(), code =>
        {
            var rendered = NamingTemplateRenderer.RenderSegment("{Title}", "A" + (char)code + "B", 2001);
            return rendered.All(c => !char.IsControl(c)) && rendered.Contains('A') && rendered.Contains('B');
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void A_title_with_a_line_break_is_not_rendered_with_the_line_break_shrunk_case()
    {
        Assert.DoesNotContain('\n', NamingTemplateRenderer.RenderSegment("{Title}", "A\nB", 2001));
        Assert.DoesNotContain('\n', NamingTemplateRenderer.RenderSegment("{Title}", "A\nB", 2001, replaceIllegalCharacters: false));
        Assert.DoesNotContain('\n', NamingTemplateRenderer.RenderFolder("{Title}", "A\r\nB", 2001));
    }
}
