using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Deluno.Contracts;
using Deluno.Security.Contracts;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// When the recycle bin's list cannot be read, the screen is told why in everyday words and what was kept: the answer on the shipped
/// request pipeline is not a bare "An unexpected error occurred." with a 500, because the person needs the reason to act (coverage
/// register, engines). Refs #1296.
/// </summary>
public sealed class ADamagedRecycleBinListSaysSoInPlainWordsTests
{
    [Fact]
    public async Task A_list_that_was_cut_short_is_answered_in_plain_words_and_left_as_it_was()
    {
        await using var host = await SharedPipelineHost.StartAsync();
        var created = await host.Client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest("owner", "Owner", "Deluno-Test-2026!"));
        created.EnsureSuccessStatusCode();
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await created.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken);
        var state = Path.Combine(host.DataRoot, "recycle-bin", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        const string cutShort = "{\"settings\":{\"retentionDays\":30},\"items\":[{\"id\":\"ab";
        await File.WriteAllTextAsync(state, cutShort);

        var response = await host.Client.GetAsync("/api/recycle-bin");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("An unexpected error occurred.", body, StringComparison.Ordinal);
        Assert.Contains("recycle bin", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("damaged", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(cutShort, await File.ReadAllTextAsync(state));
    }
}
