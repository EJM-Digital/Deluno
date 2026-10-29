using Deluno.Security;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace Deluno.Platform.Tests.Security;

/// <summary>#1433: who is asked for the current password, and what a wrong or missing one is told.</summary>
public sealed class CurrentPasswordGateTests
{
    private static readonly UserItem Owner = new("u1", "owner", "Owner", "OW", "stamp", DateTimeOffset.UtcNow);

    private static DefaultHttpContext Context(string? password)
    {
        var context = new DefaultHttpContext();
        if (password is not null)
        {
            context.Request.Headers[CurrentPasswordGate.Header] = password;
        }

        return context;
    }

    private static Mock<ISecurityRepository> Repository(bool passwordIsRight)
    {
        var repository = new Mock<ISecurityRepository>();
        repository.Setup(r => r.GetSoleUserAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Owner);
        repository.Setup(r => r.VerifyUserPasswordAsync("u1", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(passwordIsRight);
        return repository;
    }

    [Fact]
    public async Task A_caller_that_is_an_api_key_is_not_asked()
    {
        var context = Context(null);
        context.Items["deluno.apiKey"] = new ApiKeyItem("k1", "Key", "prefix", "system", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var repository = new Mock<ISecurityRepository>(MockBehavior.Strict);

        Assert.Null(await CurrentPasswordGate.RequireAsync(context, repository.Object, CancellationToken.None));
    }

    [Fact]
    public async Task An_install_with_no_account_yet_is_not_asked()
    {
        var repository = new Mock<ISecurityRepository>();
        repository.Setup(r => r.GetSoleUserAsync(It.IsAny<CancellationToken>())).ReturnsAsync((UserItem?)null);

        Assert.Null(await CurrentPasswordGate.RequireAsync(Context(null), repository.Object, CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_password_is_refused_in_plain_words_without_checking_anything()
    {
        var repository = Repository(passwordIsRight: true);

        var refusal = await CurrentPasswordGate.RequireAsync(Context(null), repository.Object, CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(refusal);
        var errors = Assert.IsType<HttpValidationProblemDetails>(problem.ProblemDetails).Errors;
        Assert.Equal("Enter your current password to make this change.", errors["currentPassword"][0]);
        repository.Verify(r => r.VerifyUserPasswordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_wrong_password_is_refused_and_a_right_one_goes_ahead()
    {
        var wrong = await CurrentPasswordGate.RequireAsync(Context("nope"), Repository(false).Object, CancellationToken.None);
        var errors = Assert.IsType<HttpValidationProblemDetails>(Assert.IsType<ProblemHttpResult>(wrong).ProblemDetails).Errors;
        Assert.Equal("Current password is not correct.", errors["currentPassword"][0]);

        Assert.Null(await CurrentPasswordGate.RequireAsync(Context("right"), Repository(true).Object, CancellationToken.None));
    }
}
