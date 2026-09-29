using System.Net;
using System.Text;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #986: the bundled snapshot of Prowlarr's tracker definitions. Every file
/// is read by Deluno's own tracker engine; these tests show how many load,
/// that the ones that do not are listed with a plain reason, and that three
/// real definitions (public HTML, public JSON, a private form sign-in) run
/// end to end against recorded page shapes.
/// </summary>
public sealed class BundledDefinitionsTests
{
    private static TrackerDefinitionCatalog NewCatalog(string? dataFolder = null)
        => new(dataFolder, NullLogger<TrackerDefinitionCatalog>.Instance);

    [Fact]
    public void The_bundled_snapshot_carries_hundreds_of_public_semi_private_and_private_sites()
    {
        var all = TrackerDefinitionCatalog.Bundled().Select(item => TrackerDefinition.Parse(item.Yaml)).ToArray();

        Assert.True(all.Length >= 560, $"only {all.Length} bundled definitions");
        Assert.True(all.Count(definition => definition.Type == "public") >= 70);
        Assert.True(all.Count(definition => definition.Type == "semi-private") >= 50);
        Assert.True(all.Count(definition => definition.Type == "private") >= 400);
    }

    [Fact]
    public void Almost_every_bundled_definition_loads_and_the_rest_are_listed_with_why()
    {
        var catalog = NewCatalog();
        var all = catalog.List();
        var bundled = all.Where(summary => summary.Origin != TrackerDefinitionOrigins.Deluno).ToArray();
        var unsupported = all.Where(summary => summary.Unsupported is not null).ToArray();

        // Nothing is silently dropped: one row per definition file.
        Assert.Equal(TrackerDefinitionCatalog.Bundled().Count(), bundled.Length + all.Count(summary => summary.Origin == TrackerDefinitionOrigins.Deluno && TrackerDefinitionCatalog.Bundled().Any(item => item.Id == summary.Id)));

        // The engine runs at least 90% of them...
        Assert.True(all.Count - unsupported.Length >= 500, $"only {all.Count - unsupported.Length} of {all.Count} are usable");

        // ...and the ones it cannot are exactly the sign-ins that ask for a
        // captcha picture to be solved by hand, each with a reason in words.
        Assert.All(unsupported, summary =>
        {
            Assert.False(string.IsNullOrWhiteSpace(summary.Unsupported));
            Assert.Contains("captcha", summary.Unsupported!, StringComparison.OrdinalIgnoreCase);
            Assert.Null(catalog.Find(summary.Id));
        });
    }

    [Fact]
    public void Every_site_comes_from_the_bundled_list_except_the_two_kept_own_files()
    {
        var catalog = NewCatalog();

        foreach (var id in new[] { "yts", "thepiratebay", "limetorrents", "1337x", "nyaasi", "rutor", "internetarchive", "uindex" })
        {
            Assert.NotNull(catalog.Find(id));
            Assert.Equal(TrackerDefinitionOrigins.Bundled, catalog.List().Single(summary => summary.Id == id).Origin);
        }

        // Kept over the list's own file of the same id: an IMDb-id API and an RSS feed with the info hash.
        foreach (var id in new[] { "eztv", "torrentdownloads" })
        {
            Assert.NotNull(catalog.Find(id));
            Assert.Equal(TrackerDefinitionOrigins.Deluno, catalog.List().Single(summary => summary.Id == id).Origin);
        }
    }

    [Fact]
    public void A_definition_in_the_data_folder_replaces_the_bundled_one()
    {
        var folder = Directory.CreateTempSubdirectory("deluno-trackers-").FullName;
        try
        {
            var bundled = TrackerDefinitionCatalog.Bundled().Single(item => item.Id == "uindex").Yaml;
            File.WriteAllText(Path.Combine(folder, "uindex.yml"), bundled.Replace("name: Uindex", "name: Uindex (newer)", StringComparison.Ordinal));

            var summary = NewCatalog(folder).List().Single(item => item.Id == "uindex");

            Assert.Equal("Uindex (newer)", summary.Name);
            Assert.Equal(TrackerDefinitionOrigins.DataFolder, summary.Origin);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_listed_as_not_supported_not_hidden()
    {
        var folder = Directory.CreateTempSubdirectory("deluno-trackers-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "brokensite.yml"), "id: brokensite\nname: Broken\n");

            var summary = NewCatalog(folder).List().Single(item => item.Id == "brokensite");

            Assert.Equal("Deluno could not read this definition file.", summary.Unsupported);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Settings_named_like_secrets_are_kept_encrypted_even_when_the_definition_calls_them_text()
    {
        var setting = (string name, string type) => new TrackerSetting { Name = name, Type = type };

        Assert.True(setting("staffpass", "text").IsSecret);
        Assert.True(setting("csrf_token", "text").IsSecret);
        Assert.True(setting("alt2fatoken", "text").IsSecret);
        Assert.True(setting("authkey", "text").IsSecret);
        Assert.False(setting("username", "text").IsSecret);
        Assert.False(setting("useragent", "text").IsSecret);
        Assert.False(setting("bypass_filter", "checkbox").IsSecret);
    }

    [Fact]
    public void The_notice_travels_with_the_bundled_definitions()
    {
        var notice = TrackerDefinitionCatalog.BundledNotice();

        Assert.Contains("Prowlarr/Indexers", notice, StringComparison.Ordinal);
        Assert.Contains("Jackett", notice, StringComparison.Ordinal);
        Assert.Contains("GPL-2.0", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void A_certificate_no_authority_signed_is_accepted_only_when_the_definition_names_its_fingerprint()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=theplace.click", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(30));
        var fingerprint = certificate.GetCertHashString();
        var untrusted = System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;

        Assert.True(TrackerCertificates.Accepts([fingerprint.ToLowerInvariant()], certificate, untrusted));
        Assert.False(TrackerCertificates.Accepts(["0000000000000000000000000000000000000000"], certificate, untrusted));
        Assert.False(TrackerCertificates.Accepts([], certificate, untrusted));
        Assert.False(TrackerCertificates.Accepts([fingerprint], certificate, untrusted | System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.True(TrackerCertificates.Accepts([], certificate, System.Net.Security.SslPolicyErrors.None));
        Assert.Equal(["3e8e14f055ed071a11e7ea64e96e3c4bf058f2f9"], NewCatalog().Find("theplace")!.Certificates);
    }

    [Fact]
    public async Task A_browser_user_agent_saved_beside_a_cookie_is_the_one_the_site_sees()
    {
        var yaml = """
            id: cookiesite
            name: Cookie Site
            type: private
            links: [https://cookie.example/]
            caps:
              categorymappings: [{id: 1, cat: Movies, desc: Movies}]
              modes: {search: [q]}
            settings:
              - {name: cookie, type: text, label: Cookie}
              - {name: useragent, type: text, label: User-Agent}
            login:
              method: cookie
              inputs: {cookie: "{{ .Config.cookie }}"}
              test: {path: index.php, selector: a.logout}
            search:
              paths: [{path: browse.php}]
              rows: {selector: tr.r}
              fields:
                title: {selector: a.t}
                download: {selector: a.d, attribute: href}
            """;
        var agents = new List<string>();
        var definition = TrackerDefinition.Parse(yaml);
        var engine = new TrackerEngine((_, _) => new FakeHandler(request =>
        {
            agents.Add(string.Join(" ", request.Headers.GetValues("User-Agent")));
            return Ok("""<a class="logout">out</a><table><tr class="r"><td><a class="t">A Title</a><a class="d" href="/d/1">d</a></td></tr></table>""", "text/html");
        }), TimeProvider.System, NullLogger<TrackerEngine>.Instance);
        var connection = new TrackerConnection("i", definition, definition.Links[0], new Dictionary<string, string?> { ["cookie"] = "uid=1; pass=abc", ["useragent"] = "Mozilla/5.0 MyOwnBrowser/1.0" });

        var result = await engine.SearchAsync(connection, new TrackerQuery("anything"), CancellationToken.None);

        Assert.Single(result.Releases);
        Assert.NotEmpty(agents);
        Assert.All(agents, agent => Assert.Equal("Mozilla/5.0 MyOwnBrowser/1.0", agent));
    }

    [Fact]
    public async Task A_download_that_reads_its_hash_from_the_before_request_becomes_a_magnet()
    {
        var (engine, connection, requests) = Connect("magnetdownload", request => request.RequestUri!.AbsolutePath == "/api/json_info"
            ? Ok("""[{"id": 14152933, "name": "Night.of.the.Living.Dead.1968", "info_hash": "0123456789abcdef0123456789abcdef01234567"}]""", "application/json")
            : Ok("<html>not the hash page</html>", "text/html"));

        var grab = await engine.ResolveDownloadAsync(connection, "https://www.magnetdownload.com/info/14152933", CancellationToken.None);

        Assert.StartsWith("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", grab.MagnetUrl, StringComparison.OrdinalIgnoreCase);
        var before = Assert.Single(requests);
        Assert.Contains("hashes=14152933", before.RequestUri!.Query, StringComparison.Ordinal);
    }

    // ---- public HTML: uindex -------------------------------------------------

    [Fact]
    public async Task A_public_html_site_lists_its_releases_with_magnets_sizes_and_seeders()
    {
        var (engine, connection, requests) = Connect("uindex", request => Ok(Fixture("uindex.html"), "text/html"));

        var result = await engine.SearchAsync(connection, new TrackerQuery("Night of the Living Dead 1968", "movie", Categories: [2000]), CancellationToken.None);

        Assert.Contains("search.php?", requests.Single().RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("search=Night+of+the+Living+Dead+1968", requests.Single().RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal(2, result.Releases.Count);
        var first = result.Releases[0];
        Assert.Equal("Night.of.the.Living.Dead.1968.1080p.BluRay.x264-GRP", first.Title);
        Assert.StartsWith("magnet:?xt=urn:btih:0123456789ABCDEF", first.Link);
        Assert.Equal((long)(4.37 * 1024 * 1024 * 1024), first.Size!.Value, tolerance: 1024 * 1024);
        Assert.Equal(41, first.Seeders);
        Assert.Equal(48, first.Peers);
        Assert.Equal("https://uindex.org/details.php?id=1001", first.DetailsUrl);
        Assert.NotNull(first.PublishDate);
        Assert.Equal(1_200_000_000L, result.Releases[1].Size!.Value, tolerance: 200_000_000);
    }

    // ---- public JSON: bangumi.moe ---------------------------------------------

    [Fact]
    public async Task A_public_json_site_is_searched_with_its_own_method_and_read_from_the_json()
    {
        var (engine, connection, requests) = Connect("bangumi-moe", request => Ok(Fixture("bangumi-moe.json"), "application/json"));

        var result = await engine.SearchAsync(connection, new TrackerQuery("Example Show"), CancellationToken.None);

        var request = requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/api/v2/torrent/search", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(2, result.Releases.Count);
        var first = result.Releases[0];
        Assert.Equal("[Sub Group] Example Show - 01 [1080p][HEVC]", first.Title);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", first.InfoHash, ignoreCase: true);
        Assert.Equal(30, first.Seeders);
        Assert.Equal(34, first.Peers);
        Assert.Equal(55, first.Grabs);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 6, 10, 15, 772, TimeSpan.Zero), first.PublishDate);
        Assert.Equal("https://bangumi.moe/poster/1.jpg", first.PosterUrl);
        // The second entry has no size at all: the definition's own default (0 B) is used.
        Assert.Equal(0, result.Releases[1].Size);
    }

    // ---- private, form sign-in: aussierules -----------------------------------

    [Fact]
    public async Task A_private_site_signs_in_through_its_form_with_saved_credentials_then_searches_and_fetches_the_torrent()
    {
        var site = new FormSignInSite();
        var (engine, connection, _) = Connect("aussierules", site.Answer, new() { ["username"] = "owner", ["password"] = "secret", ["type"] = "t_name", ["dead"] = "no" });

        var result = await engine.SearchAsync(connection, new TrackerQuery("Collingwood v Carlton"), CancellationToken.None);

        Assert.Equal("owner", site.SubmittedForm!["username"]);
        Assert.Equal("secret", site.SubmittedForm["password"]);
        Assert.Contains("keywords=Collingwood+v+Carlton", site.LastSearch!.Query, StringComparison.Ordinal);
        Assert.Contains("search_type=t_name", site.LastSearch.Query, StringComparison.Ordinal);
        var release = Assert.Single(result.Releases);
        Assert.Equal("AFL 2026 Round 20 Collingwood v Carlton 1080p", release.Title);
        Assert.Equal("https://aussierul.es/download.php?id=77", release.DownloadUrl);
        Assert.Equal(9, release.Seeders);
        Assert.Contains(release.Categories, category => category / 1000 == 5);

        var grab = await engine.ResolveDownloadAsync(connection, release.DownloadUrl!, CancellationToken.None);
        Assert.Equal(FormSignInSite.Torrent, grab.TorrentFile);
        Assert.Equal(1, site.SignIns);
    }

    [Fact]
    public async Task A_private_site_that_refuses_the_password_says_so_in_plain_words()
    {
        var site = new FormSignInSite();
        var (engine, connection, _) = Connect("aussierules", site.Answer, new() { ["username"] = "owner", ["password"] = "wrong", ["type"] = "t_name", ["dead"] = "no" });

        var failure = await Assert.ThrowsAsync<TrackerException>(() => engine.SearchAsync(connection, new TrackerQuery("anything"), CancellationToken.None));

        Assert.Contains("sign-in", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ---------------------------------------------------------------

    private static (TrackerEngine Engine, TrackerConnection Connection, List<HttpRequestMessage> Requests) Connect(
        string id,
        Func<HttpRequestMessage, HttpResponseMessage> answer,
        Dictionary<string, string?>? settings = null)
    {
        var definition = NewCatalog().Find(id) ?? throw new InvalidOperationException($"{id} is not offered.");
        var requests = new List<HttpRequestMessage>();
        var engine = new TrackerEngine((_, _) => new FakeHandler(request =>
        {
            requests.Add(request);
            return answer(request);
        }), TimeProvider.System, NullLogger<TrackerEngine>.Instance);
        return (engine, new TrackerConnection("indexer-1", definition, definition.Links[0], settings ?? []), requests);
    }

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Search", "Trackers", "Fixtures", "Cardigann", name));

    private static HttpResponseMessage Ok(string body, string mediaType)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = answer(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    /// <summary>The shape of aussierul.es: a login form, a session cookie, a browse page, a .torrent.</summary>
    private sealed class FormSignInSite
    {
        public static readonly byte[] Torrent = "d8:announce30:https://aussierul.es/announcee"u8.ToArray();

        private bool signedIn;

        public Dictionary<string, string>? SubmittedForm { get; private set; }

        public Uri? LastSearch { get; private set; }

        public int SignIns { get; private set; }

        public HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var uri = request.RequestUri!;
            switch (uri.AbsolutePath)
            {
                case "/login.php" when request.Method == HttpMethod.Get:
                    return Ok("""<form action="takelogin.php" method="post"><input name="username"><input name="password" type="password"></form>""", "text/html");

                case "/takelogin.php":
                {
                    var body = request.Content!.ReadAsStringAsync().Result;
                    SubmittedForm = body.Split('&').Select(pair => pair.Split('=')).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
                    if (SubmittedForm["password"] != "secret")
                    {
                        return Ok("<table><tr><td>An error has occured!</td></tr></table>", "text/html");
                    }

                    signedIn = true;
                    SignIns++;
                    return Ok("""<a href="logout.php">Log out</a>""", "text/html");
                }

                case "/browse.php":
                    if (!signedIn)
                    {
                        return Ok("""<form action="takelogin.php"></form>""", "text/html");
                    }

                    if (uri.Query.Length > 0)
                    {
                        LastSearch = uri;
                    }

                    return Ok("""
                        <a href="logout.php">Log out</a>
                        <table id="sortabletable"><tbody>
                        <tr><th>Header</th></tr>
                        <tr>
                          <td>x</td>
                          <td><div><a href="details.php?id=77"><strong>AFL 2026 Round 20 Collingwood v Carlton 1080p</strong></a></div><div>20-09-2026 18:30</div>
                              <a href="download.php?id=77">DL</a></td>
                          <td></td><td></td>
                          <td>2.5 GB</td><td>14</td><td>9</td><td>2</td>
                        </tr>
                        </tbody></table>
                        """, "text/html");

                case "/download.php":
                    return signedIn
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Torrent) }
                        : Ok("""<form action="takelogin.php"></form>""", "text/html");

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") };
            }
        }
    }
}
