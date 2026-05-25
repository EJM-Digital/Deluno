using Deluno.Downloader.Nzb.Parser;

namespace Deluno.Downloader.Tests.Nzb;

public class NzbDocumentTests
{
    [Fact]
    public void Parses_basic_file_and_segment_layout()
    {
        const string xml = """
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file poster="p" date="1700000000" subject='[1/1] - "movie.mkv" yEnc (1/3)'>
                <groups><group>alt.binaries.test</group></groups>
                <segments>
                  <segment bytes="500000" number="1">a@x</segment>
                  <segment bytes="500000" number="2">b@x</segment>
                  <segment bytes="123456" number="3">c@x</segment>
                </segments>
              </file>
            </nzb>
            """;
        var doc = NzbDocument.Parse(xml);
        Assert.Single(doc.Files);
        var f = doc.Files[0];
        Assert.Equal("movie.mkv", f.FileName);
        Assert.Equal(3, f.Segments.Count);
        Assert.Equal(500_000 + 500_000 + 123_456, f.TotalBytes);
    }

    [Fact]
    public void Sorts_segments_by_number()
    {
        const string xml = """
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject='"x.bin" yEnc'>
                <groups><group>g</group></groups>
                <segments>
                  <segment bytes="1" number="3">c@x</segment>
                  <segment bytes="1" number="1">a@x</segment>
                  <segment bytes="1" number="2">b@x</segment>
                </segments>
              </file>
            </nzb>
            """;
        var doc = NzbDocument.Parse(xml);
        Assert.Equal(new[] { 1, 2, 3 }, doc.Files[0].Segments.Select(s => s.Number));
    }

    [Fact]
    public void Deduplicates_segments_by_message_id()
    {
        const string xml = """
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject='"x.bin" yEnc'>
                <groups><group>g</group></groups>
                <segments>
                  <segment bytes="1" number="1">a@x</segment>
                  <segment bytes="1" number="2">a@x</segment>
                  <segment bytes="1" number="3">b@x</segment>
                </segments>
              </file>
            </nzb>
            """;
        var doc = NzbDocument.Parse(xml);
        Assert.Equal(2, doc.Files[0].Segments.Count);
    }

    [Fact]
    public void Parses_meta_block_for_password_category_name()
    {
        const string xml = """
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <head>
                <meta type="password">hunter2</meta>
                <meta type="category">movies</meta>
                <meta type="name">Release.Title.2026</meta>
              </head>
              <file subject='"x.bin" yEnc'>
                <groups><group>g</group></groups>
                <segments><segment bytes="1" number="1">a@x</segment></segments>
              </file>
            </nzb>
            """;
        var doc = NzbDocument.Parse(xml);
        Assert.Equal("hunter2", doc.Meta.Password);
        Assert.Equal("movies", doc.Meta.Category);
        Assert.Equal("Release.Title.2026", doc.Meta.Name);
    }

    [Fact]
    public void Falls_back_to_filename_password_convention()
    {
        const string xml = """
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject='"release{{pw123}}.rar" yEnc'>
                <groups><group>g</group></groups>
                <segments><segment bytes="1" number="1">a@x</segment></segments>
              </file>
            </nzb>
            """;
        var doc = NzbDocument.Parse(xml);
        Assert.Equal("pw123", doc.Meta.Password);
    }

    [Fact]
    public void Classifies_par2_files()
    {
        const string xml = """
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject='"movie.mkv" yEnc'>
                <groups><group>g</group></groups>
                <segments><segment bytes="1" number="1">a@x</segment></segments>
              </file>
              <file subject='"movie.par2" yEnc'>
                <groups><group>g</group></groups>
                <segments><segment bytes="1" number="1">b@x</segment></segments>
              </file>
            </nzb>
            """;
        var doc = NzbDocument.Parse(xml);
        Assert.Single(doc.PayloadFiles);
        Assert.Single(doc.Par2Files);
        Assert.Equal("movie.par2", doc.Par2Files.First().FileName);
    }

    [Fact]
    public void Tolerates_missing_namespace()
    {
        const string xml = """
            <nzb>
              <file subject='"x.bin" yEnc'>
                <groups><group>g</group></groups>
                <segments><segment bytes="10" number="1">a@x</segment></segments>
              </file>
            </nzb>
            """;
        var doc = NzbDocument.Parse(xml);
        Assert.Single(doc.Files);
        Assert.Equal(10, doc.Files[0].TotalBytes);
    }

    [Fact]
    public void Wraps_malformed_xml_as_InvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() => NzbDocument.Parse("not xml at all"));
    }
}
