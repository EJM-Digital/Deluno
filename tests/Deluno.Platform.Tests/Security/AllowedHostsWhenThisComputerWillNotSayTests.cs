using Deluno.Security.Hardening;

namespace Deluno.Platform.Tests.Security;

public sealed class AllowedHostsWhenThisComputerWillNotSayTests
{
    [Fact]
    public void A_computer_that_will_not_give_its_name_still_offers_everything_else()
    {
        var provider = new AllowedHostsCandidateProvider(new ForwardedHostTracker(), () => throw new InvalidOperationException("no name"), () => []);

        var offered = provider.DetectCandidates(null);

        Assert.Equal(["localhost", "127.0.0.1"], offered.Select(candidate => candidate.Host));
    }

    [Fact]
    public void A_computer_whose_adapters_cannot_be_read_still_offers_its_name_and_the_rest()
    {
        var provider = new AllowedHostsCandidateProvider(
            new ForwardedHostTracker(),
            () => "media-box",
            () => throw new System.Net.NetworkInformation.NetworkInformationException());

        var offered = provider.DetectCandidates(null);

        Assert.Equal(["localhost", "127.0.0.1", "media-box"], offered.Select(candidate => candidate.Host));
        Assert.Equal("This computer's name", offered[2].Reason);
    }

    [Fact]
    public void A_name_with_nothing_in_it_is_not_offered()
    {
        var provider = new AllowedHostsCandidateProvider(new ForwardedHostTracker(), () => null, () => []);

        Assert.DoesNotContain(provider.DetectCandidates(null), candidate => candidate.Reason == "This computer's name");
    }
}
