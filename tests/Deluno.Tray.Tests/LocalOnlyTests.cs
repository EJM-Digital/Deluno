using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>#1053: nothing the installer starts listens beyond this computer.</summary>
public sealed partial class ComponentSetupTests
{
    // ---- #1053: local only ---------------------------------------------------

    private static readonly Regex AllInterfaces = new(
        @"(?<![\d.])0\.0\.0\.0(?![\d.])|(?<![:\w]):{2}(?![:\w\d])|\[::\]|(?i:\b(Address|host|interface|ControlIP|rpc-bind-address|bind-address)\b[""']?\s*[=:]\s*[""']?\*)|\*:\d",
        RegexOptions.Compiled);

    [Fact]
    public void No_start_command_or_seeded_config_binds_every_address()
    {
        // #1053: every app the installer starts listens on 127.0.0.1 only.
        foreach (var spec in BundledComponents.Load().Where(s => s.Category == BundledComponentSpec.DownloadClientCategory))
        {
            foreach (var (name, text) in TextsOf(spec))
            {
                Assert.False(AllInterfaces.IsMatch(text), $"{spec.Kind} {name} binds every address: {text}");
            }
        }
    }

    [Fact]
    public void The_checker_itself_catches_each_way_of_binding_every_address()
    {
        foreach (var bad in new[]
                 {
                     "--server 0.0.0.0:8080", "host = 0.0.0.0", "WebUI\\Address=*", "ControlIP=0.0.0.0", "\"rpc-bind-address\": \"0.0.0.0\"",
                     "--interface ::", "listen = [::]:8080", "Address = *", "--server *:8080"
                 })
        {
            Assert.True(AllInterfaces.IsMatch(bad), $"should flag: {bad}");
        }

        foreach (var good in new[] { "--server 127.0.0.1:8080", "host = 127.0.0.1", "WebUI\\Address=127.0.0.1", "\"rpc-whitelist\": \"127.0.0.1,::1\"", "--interface 127.0.0.1 --port 8112" })
        {
            Assert.False(AllInterfaces.IsMatch(good), $"should not flag: {good}");
        }
    }

    [Fact]
    public void Every_client_that_has_a_web_address_names_this_computer_in_its_start_command_or_seeded_config()
    {
        var expectations = new Dictionary<string, string>
        {
            ["sabnzbd"] = "--server 127.0.0.1:8080",
            ["deluge"] = "--interface 127.0.0.1",
            ["qbittorrent"] = "WebUI\\Address=127.0.0.1",
            ["nzbget"] = "ControlIP=127.0.0.1",
            ["transmission"] = "127.0.0.1"
        };

        foreach (var (kind, expected) in expectations)
        {
            var spec = Load(kind);
            Assert.True(
                TextsOf(spec).Any(t => t.Text.Contains(expected, StringComparison.Ordinal)),
                $"{kind} does not say {expected}.");
        }

        // The seeded SABnzbd config binds locally too, not only its command line.
        Assert.Contains("host = 127.0.0.1", Load("sabnzbd").ConfigFileContent);
    }

    private static IEnumerable<(string Name, string Text)> TextsOf(BundledComponentSpec spec)
    {
        if (spec.PostInstallArgs is not null) yield return ("postInstallArgs", spec.PostInstallArgs);
        if (spec.PreStartArgs is not null) yield return ("preStartArgs", spec.PreStartArgs);
        if (spec.ConfigFileContent is not null) yield return ("configFileContent", spec.ConfigFileContent);
        foreach (var (from, to) in spec.ConfigReplacements ?? new Dictionary<string, string>())
        {
            yield return ("configReplacement " + from, to);
        }
    }

}
