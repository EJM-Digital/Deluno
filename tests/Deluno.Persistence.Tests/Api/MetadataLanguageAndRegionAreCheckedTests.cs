using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1394: the details language and ratings region are refused unless they are known codes, with one plain sentence
/// on the field, and a good value of each saves and reads back. A patch that does not send them is never held up by them.
/// </summary>
public sealed class MetadataLanguageAndRegionAreCheckedTests
{
    [Theory]
    [InlineData("metadataLanguage", "english", "Details language must be a language code")]
    [InlineData("metadataLanguage", "en-ZZ", "Details language must be a language code")]
    [InlineData("metadataLanguage", "xx-banana", "Details language must be a language code")]
    [InlineData("metadataLanguage", "xx-yy-zz", "Details language must be a language code")]
    [InlineData("metadataCertificationCountry", "Australia", "Ratings region must be a two-letter country code")]
    [InlineData("metadataCertificationCountry", "ZZ", "Ratings region must be a two-letter country code")]
    public async Task A_value_that_is_not_a_known_code_is_refused_on_its_field_by_patch_and_by_put(string field, string value, string sentence)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var patched = await app.Client.PatchAsJsonAsync("/api/settings", new Dictionary<string, string> { [field] = value });
        await AssertRefusedAsync(patched, field, sentence);

        var current = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        var body = current.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        body[field] = value;
        var put = await app.Client.PutAsync("/api/settings", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        await AssertRefusedAsync(put, field, sentence);

        var stored = await app.Client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.NotEqual(value, stored.GetProperty(field).GetString());
    }

    [Theory]
    [InlineData("metadataLanguage", "pt-BR", "pt-BR")]
    [InlineData("metadataLanguage", "de", "de")]
    [InlineData("metadataLanguage", "de-AT", "de-AT")]
    [InlineData("metadataCertificationCountry", "GB", "GB")]
    [InlineData("metadataCertificationCountry", "NL", "NL")]
    [InlineData("metadataCertificationCountry", " au ", "au")]
    public async Task A_known_code_saves_and_reads_back(string field, string value, string expected)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var patched = await app.Client.PatchAsJsonAsync("/api/settings", new Dictionary<string, string> { [field] = value });

        Assert.True(patched.IsSuccessStatusCode, await patched.Content.ReadAsStringAsync());
        Assert.Equal(expected, (await app.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty(field).GetString());
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string field, string sentence)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(1, errors.GetProperty(field).GetArrayLength());
        Assert.StartsWith(sentence, errors.GetProperty(field)[0].GetString(), StringComparison.Ordinal);
    }
}
