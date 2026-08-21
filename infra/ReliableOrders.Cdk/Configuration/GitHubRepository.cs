using System.Buffers;

namespace ReliableOrders.Cdk.Configuration;

/// <summary>
/// The repository whose workflows are trusted to deploy, and the claims a role's trust policy is
/// written against.
/// </summary>
/// <remarks>
/// <para>
/// A parsed type rather than a string, because the string is load-bearing in a way nothing else in
/// this app is. It becomes the <c>sub</c> claim an IAM trust policy compares a GitHub token against,
/// and the failure mode is silent: a value carrying a wildcard is a valid IAM condition, deploys
/// without complaint, and trusts workflows in repositories nobody has read.
/// </para>
/// <para>
/// So the wildcard is rejected here rather than escaped later. Everything IAM would treat as a
/// pattern is refused at synthesis, and what reaches the trust policy is a literal that
/// <c>StringEquals</c> can compare.
/// </para>
/// </remarks>
public sealed record GitHubRepository
{
    /// <summary>The issuer GitHub's workflow tokens are signed by, as the OIDC provider names it.</summary>
    /// <remarks>
    /// The host doubles as the prefix of every condition key an IAM trust policy can test, which is
    /// why it is held separately from the URL below.
    /// </remarks>
    public const string IssuerHost = "token.actions.githubusercontent.com";

    /// <summary>The provider's URL, which is what an OIDC provider resource is declared with.</summary>
    public const string IssuerUrl = $"https://{IssuerHost}";

    /// <summary>The audience AWS accepts, and the one the workflow asks GitHub for.</summary>
    /// <remarks>
    /// A token minted for anything else is a token for another service. The trust policy demands this
    /// value as well as the subject, so a token GitHub issued for a third party cannot be replayed
    /// against this account.
    /// </remarks>
    public const string Audience = "sts.amazonaws.com";

    /// <summary>What GitHub allows in an owner or a repository name.</summary>
    /// <remarks>
    /// Deliberately narrower than a rejection of <c>*</c> alone. A condition key is matched literally
    /// by <c>StringEquals</c>, so anything outside this set is either a value GitHub could never
    /// issue or a pattern somebody expected to be interpreted, and both are worth failing on.
    /// </remarks>
    private static readonly SearchValues<char> PermittedCharacters = SearchValues.Create(
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._");

    private GitHubRepository(string owner, string name)
    {
        Owner = owner;
        Name = name;
    }

    /// <summary>The account or organisation the repository belongs to.</summary>
    public string Owner { get; }

    /// <summary>The repository's own name.</summary>
    public string Name { get; }

    /// <summary>Owner and name, as GitHub writes them and as the claim carries them.</summary>
    public string FullName => $"{Owner}/{Name}";

    /// <summary>
    /// Reads an <c>owner/name</c> pair.
    /// </summary>
    /// <param name="value">The repository, as <c>cdk.json</c> holds it.</param>
    /// <returns>The parsed repository.</returns>
    /// <exception cref="ArgumentException">
    /// The value is not one owner and one name of permitted characters. The message quotes what was
    /// given and says what it would have cost.
    /// </exception>
    public static GitHubRepository Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var parts = value.Split('/');

        if (parts.Length != 2)
        {
            throw new ArgumentException(
                $"'{value}' is not a repository. Give it as owner/name, such as "
                + "octocat/hello-world, so the trust policy names one repository.",
                nameof(value));
        }

        foreach (var part in parts)
        {
            Require(part, value);
        }

        return new GitHubRepository(parts[0], parts[1]);
    }

    /// <summary>
    /// The subject claim a job carries when it deploys to a named GitHub environment.
    /// </summary>
    /// <param name="environmentName">The GitHub deployment environment, such as <c>dev</c>.</param>
    /// <returns>The <c>sub</c> value a trust policy compares against.</returns>
    /// <exception cref="ArgumentException">The name is blank or carries a wildcard.</exception>
    /// <remarks>
    /// <para>
    /// The environment is what makes the subject worth trusting. A job that names no environment
    /// carries a subject built from its ref or its pull request instead, so a trust policy written
    /// this way cannot be satisfied by a workflow that skipped the environment — and it is the
    /// environment that carries the branch and tag policies and the reviewers.
    /// </para>
    /// <para>
    /// The name is checked rather than the characters GitHub allows in one, because an environment
    /// may hold spaces and punctuation this type has no business refusing. What it refuses is the
    /// wildcard, for the reason the type comment gives.
    /// </para>
    /// </remarks>
    public string SubjectForEnvironment(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        if (environmentName.Contains('*', StringComparison.Ordinal)
            || environmentName.Contains('?', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Environment '{environmentName}' carries a wildcard. IAM would compare it literally "
                + "or match more than one environment depending on the operator, and neither is what "
                + "a deployment role should trust.",
                nameof(environmentName));
        }

        return $"repo:{FullName}:environment:{environmentName}";
    }

    /// <summary>
    /// Fails on an owner or a name holding anything GitHub would not issue.
    /// </summary>
    /// <param name="part">The owner or the name.</param>
    /// <param name="value">The whole value, so the message quotes what was written.</param>
    private static void Require(string part, string value)
    {
        if (part.Length == 0)
        {
            throw new ArgumentException(
                $"'{value}' leaves one half of the pair empty. Give it as owner/name.",
                nameof(value));
        }

        if (part.AsSpan().ContainsAnyExcept(PermittedCharacters))
        {
            throw new ArgumentException(
                $"'{value}' holds a character GitHub does not allow in a repository. A wildcard is "
                + "the one that matters: IAM accepts it, the deployment succeeds, and the role ends "
                + "up trusting workflows in repositories nobody has reviewed.",
                nameof(value));
        }
    }
}
