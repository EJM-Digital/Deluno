using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Companions;
using Deluno.Integrations.Processors;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Moq;

namespace Deluno.Worker.Tests.Companions;

/// <summary>
/// #1061, #968: Weir is connected with the person's own account. Against a stubbed Weir this proves
/// the create-account and sign-in paths, a wrong password, and that the password reaches nothing but
/// Weir's own sign-in: not a stored row, not a message, not any call Deluno makes to its own stores.
/// </summary>
public sealed class WeirConnectTests : IDisposable
{
    private const string Password = "My-Own-Weir-Pass-9!";
    private const string Username = "james";
    private readonly StubWeir _weir = new();
    private readonly List<string> _invocations = [];
    private readonly Mock<IProcessorRepository> _processors = new();
    private readonly Mock<ISecurityRepository> _security = new();
    private readonly Mock<ICompanionSystem> _system = new();
    private readonly List<CreateProcessorConnectionRequest> _created = [];

    public WeirConnectTests()
    {
        _system.SetupGet(item => item.LocalAppData).Returns(Path.Combine(Path.GetTempPath(), "weir-test-local"));
        _system.Setup(item => item.FileExists(It.IsAny<string>())).Returns(true);
        _system.Setup(item => item.ListeningPortsOf(It.IsAny<string>())).Returns(() => [_weir.Port]);
        _processors.Setup(repo => repo.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => []);
        _processors.Setup(repo => repo.CreateProcessorConnectionAsync(It.IsAny<CreateProcessorConnectionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateProcessorConnectionRequest, CancellationToken>((request, _) => { _created.Add(request); _invocations.Add(JsonSerializer.Serialize(request)); })
            .ReturnsAsync((CreateProcessorConnectionRequest request, CancellationToken _) => new ProcessorConnectionItem(
                "p1", request.Name!, request.Provider!, request.SubmissionUrl!, request.AuthHeaderName!, request.Secret, true, "unknown", null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, request.HealthCheckUrl));
        _security.Setup(repo => repo.CreateApiKeyAsync(It.IsAny<CreateApiKeyRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateApiKeyRequest, CancellationToken>((request, _) => _invocations.Add(JsonSerializer.Serialize(request)))
            .ReturnsAsync(new CreatedApiKeyResponse(new ApiKeyItem("k1", "Weir", "dk_", "read,imports", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), "dk_test-key"));
    }

    public void Dispose() => _weir.Dispose();

    private WeirCompanion NewCompanion()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        return new WeirCompanion(
            _system.Object, _processors.Object, new ProcessorConnectionService(factory.Object),
            _security.Object, new Mock<ICompanionDownloader>().Object, factory.Object);
    }

    private WeirCompanion NewCompanion(Microsoft.Extensions.Time.Testing.FakeTimeProvider time)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());
        return new WeirCompanion(
            _system.Object, _processors.Object, new ProcessorConnectionService(factory.Object),
            _security.Object, new Mock<ICompanionDownloader>().Object, factory.Object)
        {
            Time = time,
            Delay = (wait, _) => { time.Advance(wait); return Task.CompletedTask; }
        };
    }

    private sealed class Notes : ICompanionProgress
    {
        public List<string> Messages { get; } = [];

        public void Report(string step, string message) => Messages.Add(message);
    }

    [Fact]
    public async Task A_Weir_that_takes_nearly_three_minutes_to_answer_after_an_update_is_still_waited_for()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var answersAt = time.GetUtcNow() + TimeSpan.FromSeconds(170);
        _weir.BootstrapAllowed = true;
        _system.Setup(item => item.ListeningPortsOf(It.IsAny<string>()))
            .Returns(() => time.GetUtcNow() >= answersAt ? [_weir.Port] : []);

        var outcome = await NewCompanion(time).ConnectAsync(Username, Password, "http://127.0.0.1:7879", CancellationToken.None);

        Assert.True(outcome.Healthy, outcome.Message);
    }

    [Fact]
    public async Task Weir_is_given_at_least_three_minutes_before_Deluno_says_it_did_not_start()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var began = time.GetUtcNow();
        _system.Setup(item => item.ListeningPortsOf(It.IsAny<string>())).Returns(() => []);

        var outcome = await NewCompanion(time).ConnectAsync(Username, Password, "http://127.0.0.1:7879", CancellationToken.None);

        Assert.False(outcome.Healthy);
        Assert.True(time.GetUtcNow() - began >= TimeSpan.FromMinutes(3), $"Gave up after {time.GetUtcNow() - began}.");
        Assert.Contains("did not start answering", outcome.Message);
    }

    [Fact]
    public async Task While_Weir_is_starting_the_install_says_so_in_plain_words()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        _weir.BootstrapAllowed = true;
        var answersAt = time.GetUtcNow() + TimeSpan.FromSeconds(60);
        _system.Setup(item => item.ListeningPortsOf(It.IsAny<string>()))
            .Returns(() => time.GetUtcNow() >= answersAt ? [_weir.Port] : []);
        var notes = new Notes();

        await NewCompanion(time).RunAsync(new CompanionRunContext("http://127.0.0.1:7879", Path.GetTempPath(), notes), CancellationToken.None);

        Assert.Contains(notes.Messages, message => message.Contains("Weir is starting; this can take a few minutes after an update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_no_account_yet_the_persons_account_is_created_in_Weir_then_used_to_link_both_ways()
    {
        _weir.BootstrapAllowed = true;

        var outcome = await NewCompanion().ConnectAsync(Username, Password, "http://127.0.0.1:7879", CancellationToken.None);

        Assert.True(outcome.Healthy, outcome.Message);
        Assert.Equal(1, _weir.Bootstraps);
        Assert.Equal((Username, Password), _weir.CreatedAccount);
        Assert.Equal(1, _weir.Logins);
        var manager = Assert.Single(_weir.ManagerRequests);
        Assert.Equal("http://127.0.0.1:7879", manager.GetProperty("base_url").GetString());
        Assert.Equal("dk_test-key", manager.GetProperty("api_key").GetString());
        var connection = Assert.Single(_created);
        Assert.Equal($"http://127.0.0.1:{_weir.Port}/api/v1/intake/webhook/deluno", connection.SubmissionUrl);
        Assert.Equal($"http://127.0.0.1:{_weir.Port}/ready", connection.HealthCheckUrl);
        Assert.Equal("weir-webhook-secret", connection.Secret);
        _security.Verify(repo => repo.CreateApiKeyAsync(It.Is<CreateApiKeyRequest>(request => request.Scopes == "read,imports"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task With_an_account_already_made_the_persons_details_sign_in_and_no_second_account_is_created()
    {
        _weir.BootstrapAllowed = false;
        _weir.ExistingAccount = (Username, Password);

        var outcome = await NewCompanion().ConnectAsync(Username, Password, "http://127.0.0.1:7879", CancellationToken.None);

        Assert.True(outcome.Healthy, outcome.Message);
        Assert.Equal(0, _weir.Bootstraps);
        Assert.Equal(1, _weir.Logins);
        Assert.Single(_created);
    }

    [Fact]
    public async Task A_wrong_password_is_told_plainly_and_nothing_is_created()
    {
        _weir.BootstrapAllowed = false;
        _weir.ExistingAccount = (Username, Password);

        var outcome = await NewCompanion().ConnectAsync(Username, "not-my-password", "http://127.0.0.1:7879", CancellationToken.None);

        Assert.False(outcome.Healthy);
        Assert.Equal("Weir did not accept that username and password.", outcome.Message);
        Assert.False(outcome.NeedsSignIn);
        Assert.Empty(_created);
        _security.Verify(repo => repo.CreateApiKeyAsync(It.IsAny<CreateApiKeyRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_weir.ManagerRequests);
    }

    [Fact]
    public async Task Missing_details_ask_for_them_and_touch_nothing()
    {
        var outcome = await NewCompanion().ConnectAsync("", null, "http://127.0.0.1:7879", CancellationToken.None);

        Assert.False(outcome.Healthy);
        Assert.True(outcome.NeedsSignIn);
        Assert.Equal(0, _weir.Logins);
        Assert.Equal(0, _weir.Bootstraps);
    }

    [Fact]
    public async Task An_already_connected_Weir_is_left_as_it_is()
    {
        _processors.Setup(repo => repo.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProcessorConnectionItem("p1", "Weir", "generic-webhook", "http://127.0.0.1:9347/api/v1/intake/webhook/deluno", "X", "s", true, "healthy", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);

        var outcome = await NewCompanion().ConnectAsync(Username, Password, "http://127.0.0.1:7879", CancellationToken.None);

        Assert.True(outcome.Healthy);
        Assert.Equal(0, _weir.Logins);
    }

    [Fact]
    public async Task The_account_state_says_whether_Weir_has_an_account_without_starting_anything()
    {
        _weir.BootstrapAllowed = true;
        var fresh = await NewCompanion().AccountStateAsync(CancellationToken.None);
        _weir.BootstrapAllowed = false;
        var made = await NewCompanion().AccountStateAsync(CancellationToken.None);

        Assert.Equal(new WeirAccountState(true, true, false), fresh);
        Assert.Equal(new WeirAccountState(true, false, true), made);
        _system.Verify(item => item.Start(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_persons_password_reaches_nothing_Deluno_stores_or_says(bool createAccount)
    {
        _weir.BootstrapAllowed = createAccount;
        _weir.ExistingAccount = createAccount ? null : (Username, Password);
        var logs = new List<string>();

        var good = await NewCompanion().ConnectAsync(Username, Password, "http://127.0.0.1:7879", CancellationToken.None);
        logs.Add(good.Message);
        var bad = await NewCompanion().ConnectAsync(Username, Password + "x", "http://127.0.0.1:7879", CancellationToken.None);
        logs.Add(bad.Message);
        // A Weir that fails part-way: the person's password must not appear in the message either.
        _weir.FailManagerCreation = true;
        _created.Clear();
        _processors.Setup(repo => repo.ListProcessorConnectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var broken = await NewCompanion().ConnectAsync(Username, Password, "http://127.0.0.1:7879", CancellationToken.None);
        logs.Add(broken.Message);

        Assert.False(broken.Healthy);
        Assert.All(logs, line => Assert.DoesNotContain(Password, line, StringComparison.Ordinal));
        Assert.All(_invocations, call => Assert.DoesNotContain(Password, call, StringComparison.Ordinal));
        Assert.All(_created, request => Assert.DoesNotContain(Password, JsonSerializer.Serialize(request) + request.Secret, StringComparison.Ordinal));
        // Nothing was written to Deluno's stores through the repositories the companion holds beyond the API key and processor connection.
        _processors.Verify(repo => repo.UpdateProcessorConnectionAsync(It.IsAny<string>(), It.IsAny<UpdateProcessorConnectionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A small Weir: the routes Deluno calls, on a loopback port.</summary>
    private sealed class StubWeir : IDisposable
    {
        // A failed Start closes the listener for good, so each attempt gets a new one.
        private HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public StubWeir()
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var candidate = Random.Shared.Next(20000, 60000);
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
                    _listener.Start();
                    Port = candidate;
                    break;
                }
                catch (HttpListenerException)
                {
                }
            }

            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }
        public bool BootstrapAllowed { get; set; }
        public bool FailManagerCreation { get; set; }
        public (string User, string Password)? ExistingAccount { get; set; }
        public (string User, string Password)? CreatedAccount { get; private set; }
        public int Bootstraps { get; private set; }
        public int Logins { get; private set; }
        public List<JsonElement> ManagerRequests { get; } = [];

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    Handle(context);
                }
                catch (Exception)
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath;
            JsonElement body = default;
            if (context.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var text = reader.ReadToEnd();
                body = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
            }

            (int Status, string Json) reply = (path, context.Request.HttpMethod) switch
            {
                ("/api/v1/auth/bootstrap/status", "GET") => (200, $$"""{"bootstrap_allowed": {{BootstrapAllowed.ToString().ToLowerInvariant()}}}"""),
                ("/api/v1/auth/csrf", "GET") => (200, """{"csrf_token":"csrf-1"}"""),
                ("/api/v1/auth/bootstrap", "POST") => Bootstrap(body),
                ("/api/v1/auth/login", "POST") => Login(body),
                ("/api/v1/media-managers/connections", "POST") => FailManagerCreation ? (500, """{"detail":"boom"}""") : Manager(body),
                ("/api/v1/media-managers/connections/7/webhook-secret", "POST") => (200, """{"webhook_url_path":"/api/v1/intake/webhook/deluno","header_name":"X-Webhook-Secret","webhook_secret":"weir-webhook-secret"}"""),
                ("/ready", "GET") => (200, """{"ready":true}"""),
                ("/api/v1/intake/capabilities", "GET") => (200, """{"machine_name":"TEST-BOX"}"""),
                _ => (404, "{}")
            };
            var bytes = Encoding.UTF8.GetBytes(reply.Json);
            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = "application/json";
            context.Response.OutputStream.Write(bytes);
            context.Response.Close();
        }

        private (int, string) Bootstrap(JsonElement body)
        {
            if (!BootstrapAllowed)
            {
                return (409, "{}");
            }

            Bootstraps++;
            CreatedAccount = (body.GetProperty("username").GetString()!, body.GetProperty("password").GetString()!);
            ExistingAccount = CreatedAccount;
            BootstrapAllowed = false;
            return (200, "{}");
        }

        private (int, string) Login(JsonElement body)
        {
            var account = ExistingAccount;
            if (account is null || body.GetProperty("username").GetString() != account.Value.User || body.GetProperty("password").GetString() != account.Value.Password)
            {
                return (401, """{"detail":"Invalid credentials"}""");
            }

            Logins++;
            return (200, "{}");
        }

        private (int, string) Manager(JsonElement body)
        {
            ManagerRequests.Add(body);
            return (200, """{"id":7}""");
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }
}
