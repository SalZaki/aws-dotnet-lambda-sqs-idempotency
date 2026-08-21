using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Constructs;
using ReliableOrders.Cdk.Configuration;

namespace ReliableOrders.Cdk.Stacks;

/// <summary>
/// The identities GitHub Actions assumes to deploy, and the trust that decides who may assume them.
/// </summary>
/// <remarks>
/// <para>
/// A stack of its own, deployed once by a human with administrative credentials, because it is what
/// the pipeline stands on. A workflow that could deploy this stack could widen the trust policy that
/// admits it, and the audit question — who may deploy — would then be answered by whoever last
/// pushed to a branch.
/// </para>
/// <para>
/// It grants no service permissions. The CDK bootstrap already holds the roles CloudFormation
/// deploys through, so the whole grant is permission to assume those two roles and to read the
/// bootstrap's version. What each deployment may create is the bootstrap's decision, taken once for
/// the account and constrained further by a permissions boundary where one is configured, rather
/// than a policy in this file that would have to be widened for every resource a later story adds.
/// </para>
/// <para>
/// See <see cref="Configuration.GitHubRepository"/> for the subject each role trusts and why the
/// deployment environment is the part that makes it worth trusting.
/// </para>
/// </remarks>
public sealed class DeploymentIdentityStack : Stack
{
    /// <summary>The GitHub deployment environment the development deployment runs in.</summary>
    /// <remarks>
    /// The same name the stack is deployed under, so an operator reading a deployment in the GitHub
    /// UI and an operator reading CloudFormation are reading about the same environment.
    /// </remarks>
    public const string DevelopmentEnvironmentName = "dev";

    /// <summary>The GitHub deployment environment a tagged release deploys through.</summary>
    /// <remarks>
    /// Named for the workflow rather than for a target, because it is the approval and the tag policy
    /// that distinguish it. Today it deploys the same environment as the workflow above; what it
    /// carries that the other does not is a reviewer and a ref policy that admits only a version tag.
    /// </remarks>
    public const string ReleaseEnvironmentName = "release";

    /// <summary>
    /// The qualifier the CDK bootstrap names its roles with, when nothing overrides it.
    /// </summary>
    /// <remarks>
    /// A bootstrap deployed with <c>--qualifier</c> names its roles differently, and this stack would
    /// then grant permission to assume roles that do not exist — reported at deployment time as
    /// access denied on a role nobody recognises. Set <c>@aws-cdk/core:bootstrapQualifier</c> in
    /// <c>cdk.json</c> in that case, which is the same context key the CLI reads, so the grant and
    /// the deployment cannot disagree.
    /// </remarks>
    public const string DefaultBootstrapQualifier = "hnb659fds";

    /// <summary>The context key the CDK CLI reads a non-default bootstrap qualifier from.</summary>
    public const string BootstrapQualifierContextKey = "@aws-cdk/core:bootstrapQualifier";

    /// <summary>
    /// The thumbprint of the certificate authority behind GitHub's issuer.
    /// </summary>
    /// <remarks>See <see cref="DeclareProvider"/> for why it is declared and what verifies the
    /// token in practice.</remarks>
    private const string GitHubIssuerThumbprint = "6938fd4d98bab03faadb97b34396831e3780aea1";

    /// <summary>
    /// Declares the provider, the two roles, and what they may do.
    /// </summary>
    /// <param name="scope">The CDK application.</param>
    /// <param name="id">The stack name.</param>
    /// <param name="repository">The repository whose workflows are trusted.</param>
    /// <param name="existingOidcProviderArn">
    /// An OIDC provider for GitHub that the account already holds, or null to declare one. An account
    /// may hold exactly one provider per issuer, and a second is rejected by IAM as an entity that
    /// already exists — so an account that reached GitHub OIDC through another repository first has
    /// to import that one rather than deploy this stack twice.
    /// </param>
    /// <param name="props">Account, Region and stack-level settings.</param>
    public DeploymentIdentityStack(
        Construct scope,
        string id,
        GitHubRepository repository,
        string? existingOidcProviderArn = null,
        IStackProps? props = null)
        : base(scope, id, props)
    {
        ArgumentNullException.ThrowIfNull(repository);

        // The provider names the issuer, so an ARN is half of the same decision the subject is the
        // other half of — and this half arrives as a string from context. A valid ARN for some other
        // provider in the account would deploy a role that trusts that issuer's tokens whenever a
        // sub and an aud happened to match, which is a trust nobody wrote down and nothing reports.
        if (existingOidcProviderArn is not null
            && !existingOidcProviderArn.EndsWith($":oidc-provider/{GitHubRepository.IssuerHost}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{existingOidcProviderArn}' is not a provider for {GitHubRepository.IssuerHost}. "
                + "An ARN naming another issuer would be trusted for tokens this repository never "
                + "asked for.",
                nameof(existingOidcProviderArn));
        }

        var tags = Amazon.CDK.Tags.Of(this);
        tags.Add("Project", ReliableOrdersStack.ProjectTagValue);
        tags.Add("ManagedBy", ReliableOrdersStack.ManagedByTagValue);

        var providerArn = existingOidcProviderArn ?? DeclareProvider();

        var development = DeploymentRole(
            "DevelopmentDeploymentRole",
            providerArn,
            repository,
            DevelopmentEnvironmentName,
            "Deploys the development stack from a push to the default branch.");

        var release = DeploymentRole(
            "ReleaseDeploymentRole",
            providerArn,
            repository,
            ReleaseEnvironmentName,
            "Deploys a version tag, after the release environment's reviewers approve it.");

        // The ARNs rather than the names, because that is what the workflow's secret holds and what
        // the action assumes. They are outputs rather than fixed names for the reason the tables are:
        // a name written here is a name that cannot be changed without replacing the role, and
        // nothing needs to predict it — the setup script reads these.
        _ = new CfnOutput(this, "DevelopmentDeploymentRoleArn", new CfnOutputProps
        {
            Value = development.RoleArn,
            Description = $"Assumed by a job running in the {DevelopmentEnvironmentName} environment.",
        });

        _ = new CfnOutput(this, "ReleaseDeploymentRoleArn", new CfnOutputProps
        {
            Value = release.RoleArn,
            Description = $"Assumed by a job running in the {ReleaseEnvironmentName} environment.",
        });

        // Output whether it was declared here or imported, so the next account-level stack that needs
        // it reads one value rather than deciding which case it is in.
        _ = new CfnOutput(this, "GitHubOidcProviderArn", new CfnOutputProps
        {
            Value = providerArn,
            Description = "The provider GitHub's workflow tokens are verified against.",
        });
    }

    /// <summary>
    /// Declares the OIDC provider for GitHub's token issuer.
    /// </summary>
    /// <returns>The provider's ARN.</returns>
    /// <remarks>
    /// <para>
    /// The L1 resource rather than <c>OpenIdConnectProvider</c>. The L2 is backed by a custom
    /// resource, which would put a Lambda function and a role of its own into the one stack whose
    /// whole subject is who may deploy — and CloudFormation has declared this resource type natively
    /// since long before this app was written.
    /// </para>
    /// <para>
    /// The thumbprint is IAM's legacy trust anchor and is no longer what IAM verifies for this
    /// issuer: certificates for a provider hosted by a well-known certificate authority are validated
    /// against IAM's own trust store, and GitHub's is. It is declared because the value is not
    /// optional to a reader — leaving the list empty invites the conclusion that verification was
    /// forgotten — and because a provider carrying a thumbprint costs nothing where it is ignored.
    /// </para>
    /// </remarks>
    private string DeclareProvider()
    {
        var provider = new CfnOIDCProvider(this, "GitHubOidcProvider", new CfnOIDCProviderProps
        {
            Url = GitHubRepository.IssuerUrl,
            ClientIdList = [GitHubRepository.Audience],
            ThumbprintList = [GitHubIssuerThumbprint],
        });

        return provider.AttrArn;
    }

    /// <summary>
    /// A role one GitHub deployment environment may assume, and nothing else may.
    /// </summary>
    /// <param name="id">The construct identifier.</param>
    /// <param name="providerArn">The OIDC provider the token is verified against.</param>
    /// <param name="repository">The repository the subject is built from.</param>
    /// <param name="environmentName">The GitHub deployment environment the job runs in.</param>
    /// <param name="description">What assumes it, as an operator reading the console would ask.</param>
    /// <returns>The role.</returns>
    /// <remarks>
    /// <para>
    /// Both conditions are <c>StringEquals</c> and neither is <c>StringLike</c>. The audience stops a
    /// token GitHub minted for another service being replayed here, and the subject is the whole of
    /// the authorisation decision — so it is compared literally, and
    /// <see cref="GitHubRepository.SubjectForEnvironment"/> refuses a value that would have made the
    /// comparison a pattern.
    /// </para>
    /// <para>
    /// The session lasts an hour, which is the shortest AWS allows and longer than a deployment. The
    /// action requests credentials for the job it runs in and the token is minted per job, so nothing
    /// here outlives the workflow that asked for it.
    /// </para>
    /// </remarks>
    private Role DeploymentRole(
        string id,
        string providerArn,
        GitHubRepository repository,
        string environmentName,
        string description)
    {
        var role = new Role(this, id, new RoleProps
        {
            Description = description,
            MaxSessionDuration = Duration.Hours(1),
            AssumedBy = new WebIdentityPrincipal(
                providerArn,
                new Dictionary<string, object>
                {
                    ["StringEquals"] = new Dictionary<string, object>
                    {
                        [$"{GitHubRepository.IssuerHost}:aud"] = GitHubRepository.Audience,
                        [$"{GitHubRepository.IssuerHost}:sub"] =
                            repository.SubjectForEnvironment(environmentName),
                    },
                }),
        });

        // Named resources, no wildcard. The image-publish and lookup roles are deliberately absent:
        // this app publishes no container image and performs no context lookup, and a grant for
        // either would be a permission nothing exercises and nobody would notice going stale.
        role.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = ["sts:AssumeRole"],
            Resources = [BootstrapRoleArn("deploy"), BootstrapRoleArn("file-publish")],
        }));

        // The CLI compares the bootstrap's version against what the template requires before it
        // deploys, and reads it from this parameter. Scoped to the one parameter: it is a version
        // number, and a role that could read the parameter store is a role that could read whatever
        // else the account keeps there.
        role.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = ["ssm:GetParameter"],
            Resources =
            [
                $"arn:{Partition}:ssm:{Region}:{Account}:parameter/cdk-bootstrap/{BootstrapQualifier}/version",
            ],
        }));

        return role;
    }

    /// <summary>
    /// One of the roles the CDK bootstrap declared in this account and Region.
    /// </summary>
    /// <param name="purpose">The role's part of the name, such as <c>deploy</c>.</param>
    /// <returns>Its ARN.</returns>
    /// <remarks>
    /// Written out rather than looked up. The bootstrap stack is deployed by the CDK CLI rather than
    /// by this app, so there is no reference to take, and an import would tie a synthesis that needs
    /// no AWS access to an account that has to answer.
    /// </remarks>
    private string BootstrapRoleArn(string purpose) =>
        $"arn:{Partition}:iam::{Account}:role/cdk-{BootstrapQualifier}-{purpose}-role-{Account}-{Region}";

    /// <summary>The qualifier this account's bootstrap was deployed with.</summary>
    /// <exception cref="InvalidOperationException">The context key is set to something else.</exception>
    /// <remarks>
    /// Matched as an object rather than cast, the way <c>Program.cs</c> reads the keys it takes. A
    /// cast would coalesce <c>-c @aws-cdk/core:bootstrapQualifier</c> written without a value, which
    /// is <c>true</c>, to the default — while the CLI would use <c>true</c> as the qualifier and look
    /// for <c>cdk-true-deploy-role-…</c>. That is exactly the disagreement between the grant and the
    /// deployment that this key exists to prevent.
    /// </remarks>
    private string BootstrapQualifier =>
        Node.TryGetContext(BootstrapQualifierContextKey) switch
        {
            null => DefaultBootstrapQualifier,
            string qualifier when !string.IsNullOrWhiteSpace(qualifier) => qualifier,
            var value => throw new InvalidOperationException(
                $"The {BootstrapQualifierContextKey} context key is set to '{value}', which is not a "
                + $"qualifier. Pass it as -c {BootstrapQualifierContextKey}=<value>."),
        };
}
