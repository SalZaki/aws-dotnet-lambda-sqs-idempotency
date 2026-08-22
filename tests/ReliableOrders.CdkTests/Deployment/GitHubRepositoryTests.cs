using ReliableOrders.Cdk.Configuration;

namespace ReliableOrders.CdkTests.Deployment;

/// <summary>
/// The value that becomes a trust policy's subject claim.
/// </summary>
/// <remarks>
/// The wildcard cases are the ones worth having. Every other rejection here is a typo that would fail
/// the first deployment loudly; a wildcard deploys, and the role it leaves behind trusts workflows in
/// repositories nobody has read.
/// </remarks>
public sealed class GitHubRepositoryTests
{
    [Fact]
    public void The_subject_names_the_repository_and_the_environment()
    {
        var repository = GitHubRepository.Parse("octocat/hello-world");

        Assert.Equal(
            "repo:octocat/hello-world:environment:dev",
            repository.SubjectForEnvironment("dev"));
    }

    [Theory]
    [InlineData("octocat/*")]
    [InlineData("*/hello-world")]
    [InlineData("octocat/hello-world*")]
    [InlineData("octocat/hello?world")]
    [InlineData("octocat")]
    [InlineData("octocat/hello-world/main")]
    [InlineData("octocat/")]
    [InlineData("/hello-world")]
    [InlineData("octocat hello/world")]
    public void A_value_that_is_not_one_repository_is_refused(string value)
    {
        _ = Assert.Throws<ArgumentException>(() => GitHubRepository.Parse(value));
    }

    /// <summary>
    /// A wildcard environment is refused for the same reason a wildcard repository is.
    /// </summary>
    /// <remarks>
    /// The environment reaches the same claim, and it is the half that carries the reviewers and the
    /// ref policy. A subject ending <c>:environment:*</c> would be satisfied by an environment created
    /// later with no policies on it at all.
    /// </remarks>
    [Theory]
    [InlineData("*")]
    [InlineData("re*")]
    [InlineData("dev?")]
    public void A_wildcard_environment_is_refused(string environmentName)
    {
        var repository = GitHubRepository.Parse("octocat/hello-world");

        _ = Assert.Throws<ArgumentException>(() => repository.SubjectForEnvironment(environmentName));
    }

    /// <summary>
    /// The repository this app is configured with is a real one.
    /// </summary>
    /// <remarks>
    /// Read out of cdk.json rather than repeated here. The context value is what synthesis uses, and
    /// a test naming its own would pass over a cdk.json nobody could deploy.
    /// </remarks>
    [Fact]
    public void The_configured_repository_parses()
    {
        var repository = GitHubRepository.Parse(DeploymentIdentityStackTests.ConfiguredRepository());

        Assert.NotEmpty(repository.Owner);
        Assert.NotEmpty(repository.Name);
    }
}
