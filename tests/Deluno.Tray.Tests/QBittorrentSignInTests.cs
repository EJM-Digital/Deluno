using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1050: qBittorrent 5.2.1 needs credentials; the hash format and where the password is kept.</summary>
public sealed partial class ComponentSetupTests
{
    // ---- #1050: qBittorrent's password hash ---------------------------------

    [Fact]
    public void The_qBittorrent_hash_matches_a_value_computed_independently()
    {
        // Computed with Node's crypto.pbkdf2Sync('adminadmin', salt 0..15, 100000, 64, 'sha512'),
        // not with this code. Format from qBittorrent's src/base/utils/password.cpp
        // at release-5.2.1: base64(salt) + ':' + base64(key), PBKDF2-HMAC-SHA512,
        // 100000 iterations, 16-byte salt, 64-byte key.
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

        var value = QBittorrentPassword.ToIniValue("adminadmin", salt);

        Assert.Equal(
            "@ByteArray(AAECAwQFBgcICQoLDA0ODw==:UC4grCR8uxnR4Gzyoule02IP9X8mRb7SaEfCylvs19kyzc76Vdb/0xQlSKu43sz2Dl4vz2SIZymUO3PvTxIfVQ==)",
            value);
    }

    [Fact]
    public void A_random_hash_verifies_the_way_qBittorrent_verifies_it()
    {
        var value = QBittorrentPassword.ToIniValue("a generated one");
        var match = Regex.Match(value, @"^@ByteArray\((?<salt>[^:]+):(?<key>[^)]+)\)$");
        Assert.True(match.Success);

        var salt = Convert.FromBase64String(match.Groups["salt"].Value);
        var key = Convert.FromBase64String(match.Groups["key"].Value);
        Assert.Equal(16, salt.Length);
        Assert.Equal(64, key.Length);
        Assert.Equal(
            key,
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("a generated one"), salt, 100000, HashAlgorithmName.SHA512, 64));
    }

    [Fact]
    public void Two_hashes_of_the_same_password_use_different_salts()
        => Assert.NotEqual(QBittorrentPassword.ToIniValue("same"), QBittorrentPassword.ToIniValue("same"));

    [Fact]
    public void A_generated_password_is_long_and_only_letters_and_digits()
    {
        var password = GeneratedPassword.Create();

        Assert.Equal(24, password.Length);
        Assert.Matches("^[A-Za-z0-9]+$", password);
        Assert.NotEqual(password, GeneratedPassword.Create());
    }

    [Fact]
    public void The_qBittorrent_seed_carries_a_user_name_and_a_password_hash_and_binds_to_this_computer()
    {
        var qbittorrent = Load("qbittorrent");
        Assert.Equal("deluno", qbittorrent.CredentialsUsername);

        var ini = ComponentConfig.RenderPlaceholders(qbittorrent.ConfigFileContent!, new BundledComponentCredentials("deluno", "pw123"));

        Assert.Contains("WebUI\\Username=deluno", ini);
        Assert.Matches(@"WebUI\\Password_PBKDF2=""@ByteArray\([A-Za-z0-9+/=]+:[A-Za-z0-9+/=]+\)""", ini);
        Assert.Contains("WebUI\\Address=127.0.0.1", ini);
        Assert.DoesNotContain("pw123", ini);
        Assert.DoesNotContain("{{", ini);
    }

    [Fact]
    public async Task A_seeded_config_stores_the_password_for_Deluno_and_a_kept_config_does_not()
    {
        var store = new RecordingCredentialStore();
        var written = new FakeConfigWriter(result: true);
        await InstallWith(Load("qbittorrent"), written, store);

        Assert.NotNull(written.Content);
        var saved = store.Saved["qbittorrent"];
        Assert.Equal("deluno", saved.Username);
        // The plain password is in the store, only its hash is in the file.
        Assert.DoesNotContain(saved.Password, written.Content);
        var hash = Regex.Match(written.Content!, @"@ByteArray\((?<salt>[^:]+):(?<key>[^)]+)\)");
        Assert.Equal(
            Convert.FromBase64String(hash.Groups["key"].Value),
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(saved.Password), Convert.FromBase64String(hash.Groups["salt"].Value), 100000, HashAlgorithmName.SHA512, 64));

        var keptStore = new RecordingCredentialStore();
        await InstallWith(Load("qbittorrent"), new FakeConfigWriter(result: false), keptStore);
        Assert.Empty(keptStore.Saved);
    }

}
