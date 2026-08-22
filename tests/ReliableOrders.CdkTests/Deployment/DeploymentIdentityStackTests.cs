using System.Text.Json;
using Amazon.CDK;
using Amazon.CDK.Assertions;
using ReliableOrders.Cdk.Configuration;
using ReliableOrders.Cdk.Stacks;

namespace ReliableOrders.CdkTests.Deployment;

/// <summary>
/// The trust policy that decides who may deploy, and the little it grants once it has decided.
/// </summary>
/// <remarks>
/// This stack is deployed by hand and rarely, which is exactly why its template is asserted. A
/// mistake in it is not caught by a deployment failing — a trust policy that is too wide deploys
/// cleanly, works, and is only wrong in a way nothing reports.
/// </remarks>
public sealed class DeploymentIdentityStackTests
{
    /// <summary>The type CloudFormation declares the provider as.</summary>
    private const string ProviderResourceType = "AWS::IAM::OIDCProvider";

    /// <summary>The type both roles are declared as.</summary>
    private const string RoleResourceType = "AWS::IAM::Role";

    /// <summary>A provider some other stack in the account declared first.</summary>
    private const string ImportedProviderArn =
        "arn:aws:iam::111122223333:oidc-provider/token.actions.githubusercontent.com";

    /// <summary>
    /// Each role trusts one repository, in one environment, holding a token minted for AWS.
    /// </summary>
    /// <remarks>
    /// Rendered JSON rather than a match on the structure, because the claim is that these exact
    /// strings are what IAM compares. The subject is read back whole: a case asserting that the
    /// document merely contains the repository would pass on a policy that also matched every branch
    /// in it.
    /// </remarks>
    [Theory]
    [InlineData(DeploymentIdentityStack.DevelopmentEnvironmentName)]
    [InlineData(DeploymentIdentityStack.ReleaseEnvironmentName)]
    public void A_role_trusts_one_environment_of_one_repository(string environmentName)
    {
        var trust = TrustPolicyFor(Template(), environmentName);

        Assert.Contains(
            $"\"{GitHubRepository.IssuerHost}:sub\":\"repo:{Repository.FullName}:environment:{environmentName}\"",
            trust,
            StringComparison.Ordinal);

        Assert.Contains(
            $"\"{GitHubRepository.IssuerHost}:aud\":\"{GitHubRepository.Audience}\"",
            trust,
            StringComparison.Ordinal);

        Assert.Contains("\"StringEquals\"", trust, StringComparison.Ordinal);
        Assert.Contains("\"sts:AssumeRoleWithWebIdentity\"", trust, StringComparison.Ordinal);
    }

    /// <summary>
    /// No trust policy is written as a pattern, and none carries a wildcard.
    /// </summary>
    /// <remarks>
    /// <c>StringLike</c> is the operator that turns the subject into a pattern, and a <c>*</c> under
    /// <c>StringEquals</c> is the mistake of someone who expected it to be one. Both are refused here
    /// as well as in <see cref="GitHubRepository"/>, because a later edit could reach the condition
    /// dictionary without going through that type.
    /// </remarks>
    [Fact]
    public void No_role_is_trusted_by_a_pattern()
    {
        var template = Template();

        foreach (var environmentName in Environments)
        {
            var trust = TrustPolicyFor(template, environmentName);

            Assert.DoesNotContain("StringLike", trust, StringComparison.Ordinal);
            Assert.DoesNotContain("*", trust, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The grant is permission to assume the bootstrap's roles, and nothing else.
    /// </summary>
    /// <remarks>
    /// What a deployment may create is the bootstrap's decision. This asserts the boundary that makes
    /// that true: a policy here that granted a service action directly would be a permission the
    /// bootstrap's permissions boundary never sees.
    /// </remarks>
    [Fact]
    public void A_role_may_assume_the_bootstrap_roles_and_read_its_version()
    {
        var template = Template();

        // The end-to-end role is excluded by what only it holds. It reaches a run's own stack, which
        // is a different question with two cases of its own; what this one asserts is that the roles
        // that deploy the stacks people rely on hold nothing besides the bootstrap grant.
        var deploying = template.FindResources("AWS::IAM::Policy")
            .Where(resource => !JsonSerializer.Serialize(resource.Value)
                .Contains("sqs:SendMessage", StringComparison.Ordinal));

        foreach (var (logicalId, resource) in deploying)
        {
            var document = JsonSerializer.Serialize(resource);

            Assert.Contains("sts:AssumeRole", document, StringComparison.Ordinal);
            Assert.Contains(
                $"role/cdk-{DeploymentIdentityStack.DefaultBootstrapQualifier}-deploy-role-",
                document,
                StringComparison.Ordinal);
            Assert.Contains(
                $"role/cdk-{DeploymentIdentityStack.DefaultBootstrapQualifier}-file-publish-role-",
                document,
                StringComparison.Ordinal);
            Assert.Contains("parameter/cdk-bootstrap/", document, StringComparison.Ordinal);

            // Named rather than counted, for the reason the persistence grant gives: a policy can
            // carry the two statements above and a third nobody meant to write.
            foreach (var refused in new[]
                     {
                         "\"Resource\":\"*\"", "\"Action\":\"*\"", "\"sts:*", "\"iam:", "\"s3:",
                         "\"cloudformation:", "\"lambda:", "\"sqs:", "\"dynamodb:",
                     })
            {
                Assert.False(
                    document.Contains(refused, StringComparison.Ordinal),
                    $"Policy '{logicalId}' carries '{refused}'. The bootstrap holds what a deployment "
                    + "may do, and a permission granted here is one its boundary never sees.");
            }
        }
    }

    /// <summary>
    /// The end-to-end role reaches ephemeral stacks and nothing anybody is using.
    /// </summary>
    /// <remarks>
    /// The patterns are the whole of the isolation. A run's stack, queues, tables and log group all
    /// carry the ephemeral prefix, so a test that went wrong cannot drain the development queue or
    /// read its tables — and the assertion is written as the absence of the deployed environment's
    /// names rather than the presence of the prefix, because the prefix is what a mistake would keep
    /// while widening the pattern around it.
    /// </remarks>
    [Fact]
    public void The_end_to_end_role_reaches_only_ephemeral_stacks()
    {
        var policy = EndToEndPolicy();

        Assert.Contains($"{EphemeralQueuePrefix}*", policy, StringComparison.Ordinal);
        Assert.Contains($"table/{EphemeralStackPrefix}*", policy, StringComparison.Ordinal);
        Assert.Contains($"log-group:{EphemeralStackPrefix}*", policy, StringComparison.Ordinal);

        foreach (var deployed in new[]
                 {
                     $"reliable-orders-{EnvironmentConfig.Development.EnvironmentName}",
                     $"table/ReliableOrders-{EnvironmentConfig.Development.EnvironmentName}",
                     "reliable-orders-*",
                     "table/*",
                 })
        {
            Assert.DoesNotContain(deployed, policy, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// It reads what the run produced and writes only to the queue it is testing.
    /// </summary>
    /// <remarks>
    /// A run asserts on what the function wrote. A role that could write the tables could make its own
    /// assertion pass, which is the failure this holds off — the message is the only thing the test is
    /// allowed to put into the system.
    /// </remarks>
    [Fact]
    public void The_end_to_end_role_writes_nothing_it_asserts_on()
    {
        var policy = EndToEndPolicy();

        Assert.Contains("sqs:SendMessage", policy, StringComparison.Ordinal);
        Assert.Contains("dynamodb:GetItem", policy, StringComparison.Ordinal);
        Assert.Contains("logs:FilterLogEvents", policy, StringComparison.Ordinal);
        Assert.Contains("cloudwatch:GetMetricData", policy, StringComparison.Ordinal);

        foreach (var refused in new[]
                 {
                     "dynamodb:PutItem", "dynamodb:UpdateItem", "dynamodb:DeleteItem",
                     "dynamodb:TransactWriteItems", "sqs:PurgeQueue", "sqs:DeleteQueue",
                     "cloudwatch:PutMetricData", "logs:PutLogEvents", "lambda:",
                 })
        {
            Assert.DoesNotContain(refused, policy, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// An account that already trusts GitHub has its provider imported rather than declared again.
    /// </summary>
    /// <remarks>
    /// IAM allows one provider per issuer per account, and refuses a second as an entity that already
    /// exists. The failure is at deployment and it is clear, but the fix is this parameter, so the
    /// case is here to say the parameter works rather than to catch a regression.
    /// </remarks>
    [Fact]
    public void An_existing_provider_is_used_rather_than_declared_twice()
    {
        var template = Template(ImportedProviderArn);

        Assert.Empty(template.FindResources(ProviderResourceType));

        foreach (var environmentName in Environments)
        {
            Assert.Contains(ImportedProviderArn, TrustPolicyFor(template, environmentName), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A provider ARN naming another issuer is refused rather than trusted.
    /// </summary>
    /// <remarks>
    /// The ARN reaches the trust policy as the principal, so it decides which issuer's tokens the
    /// role accepts — the same kind of decision <see cref="GitHubRepository"/> exists to keep out of
    /// an unchecked string. An ARN for another provider in the account is well formed and deploys.
    /// </remarks>
    [Theory]
    [InlineData("arn:aws:iam::111122223333:oidc-provider/accounts.google.com")]
    [InlineData("arn:aws:iam::111122223333:oidc-provider/token.actions.githubusercontent.com.example.com")]
    [InlineData("arn:aws:iam::111122223333:role/not-a-provider")]
    public void A_provider_for_another_issuer_is_refused(string providerArn)
    {
        _ = Assert.Throws<ArgumentException>(() => Template(providerArn));
    }

    /// <summary>
    /// A bootstrap deployed under another qualifier is granted against that qualifier's roles.
    /// </summary>
    [Fact]
    public void The_grant_follows_the_bootstrap_qualifier()
    {
        var template = TemplateWithQualifier("custom1234");

        var policy = JsonSerializer.Serialize(template.FindResources("AWS::IAM::Policy"));

        Assert.Contains("role/cdk-custom1234-deploy-role-", policy, StringComparison.Ordinal);
        Assert.Contains("parameter/cdk-bootstrap/custom1234/version", policy, StringComparison.Ordinal);
    }

    /// <summary>
    /// A qualifier context key set to something that is not a qualifier fails synthesis.
    /// </summary>
    /// <remarks>
    /// <c>-c @aws-cdk/core:bootstrapQualifier</c> written without a value is <c>true</c>. Coalescing
    /// that to the default would grant against the default's role names while the CLI addressed the
    /// bootstrap as <c>true</c>, and the deployment would fail as access denied on a role nobody
    /// recognises.
    /// </remarks>
    [Fact]
    public void A_qualifier_that_is_not_a_qualifier_fails_synthesis()
    {
        _ = Assert.Throws<InvalidOperationException>(() => TemplateWithQualifier(true));
    }

    /// <summary>
    /// The provider names GitHub's issuer and accepts tokens minted for AWS alone.
    /// </summary>
    [Fact]
    public void The_declared_provider_names_the_issuer_and_the_audience()
    {
        var provider = Template().OnlyResource(ProviderResourceType);

        Assert.Equal(GitHubRepository.IssuerUrl, provider.Properties["Url"]);
        Assert.Equal($"[\"{GitHubRepository.Audience}\"]", provider.Json("ClientIdList"));
    }

    /// <summary>
    /// Both role ARNs are published, because the setup script reads them and nothing else can.
    /// </summary>
    [Theory]
    [InlineData("DevelopmentDeploymentRoleArn")]
    [InlineData("ReleaseDeploymentRoleArn")]
    [InlineData("EndToEndRoleArn")]
    [InlineData("GitHubOidcProviderArn")]
    public void The_stack_publishes_what_the_setup_needs(string outputName)
    {
        Template().HasOutput(outputName, Match.AnyValue());
    }

    /// <summary>
    /// The repository the app is configured with, as <c>cdk.json</c> holds it.
    /// </summary>
    /// <remarks>
    /// Read from the file rather than repeated in the tests. The context value is what synthesis uses,
    /// so a constant here would keep passing over a cdk.json that no longer deploys.
    /// </remarks>
    internal static string ConfiguredRepository()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "cdk.json");

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.GetProperty("context").GetProperty("githubRepository").GetString()
            ?? throw new InvalidOperationException("cdk.json sets githubRepository to null.");
    }

    /// <summary>The environments the stack declares a role for.</summary>
    private static readonly string[] Environments =
    [
        DeploymentIdentityStack.DevelopmentEnvironmentName,
        DeploymentIdentityStack.ReleaseEnvironmentName,
        DeploymentIdentityStack.EndToEndEnvironmentName,
    ];

    /// <summary>What every ephemeral stack and queue is named for.</summary>
    private static string EphemeralStackPrefix => $"ReliableOrders-{EnvironmentConfig.EphemeralPrefix}";

    private static string EphemeralQueuePrefix => $"reliable-orders-{EnvironmentConfig.EphemeralPrefix}";

    /// <summary>
    /// The end-to-end role's policy, rendered.
    /// </summary>
    /// <remarks>
    /// Found by the actions only it holds rather than by a logical ID, for the reason
    /// <c>SynthesizedStack</c> gives. The other two roles carry the bootstrap grant and nothing else,
    /// so the send is what distinguishes this one.
    /// </remarks>
    private static string EndToEndPolicy()
    {
        var matches = Template().FindResources("AWS::IAM::Policy")
            .Select(resource => JsonSerializer.Serialize(resource.Value))
            .Where(policy => policy.Contains("sqs:SendMessage", StringComparison.Ordinal))
            .ToArray();

        return Assert.Single(matches);
    }

    /// <summary>The repository under test, which is the one the app is configured with.</summary>
    private static GitHubRepository Repository => GitHubRepository.Parse(ConfiguredRepository());

    /// <summary>
    /// The trust policy of the role that trusts a given environment.
    /// </summary>
    /// <remarks>
    /// The role is found by the environment in its own trust policy rather than by a logical ID, for
    /// the reason <c>SynthesizedStack</c> gives: an ID pinned here would fail on a rename that
    /// deployed identically.
    /// </remarks>
    private static string TrustPolicyFor(Template template, string environmentName)
    {
        var marker = $":environment:{environmentName}\"";

        var matches = template.FindResources(RoleResourceType)
            .Select(resource => JsonSerializer.Serialize(resource.Value["Properties"]))
            .Where(properties => properties.Contains(marker, StringComparison.Ordinal))
            .ToArray();

        return Assert.Single(matches);
    }

    /// <summary>
    /// Synthesises against an app whose bootstrap qualifier context is set to the given value.
    /// </summary>
    private static Template TemplateWithQualifier(object qualifier)
    {
        // Built on the deployed context rather than an empty one, for the reason SynthesizedStack
        // gives: without the feature flags the CLI supplies, the partition is a token rather than a
        // literal and the stack synthesises differently from every deployment of it.
        var app = SynthesizedStack.NewApp();

        app.Node.SetContext(DeploymentIdentityStack.BootstrapQualifierContextKey, qualifier);

        return Amazon.CDK.Assertions.Template.FromStack(new DeploymentIdentityStack(
            app,
            "ReliableOrders-DeploymentIdentity",
            Repository,
            existingOidcProviderArn: null,
            new StackProps { Env = SynthesizedStack.TestEnvironment }));
    }

    private static Template Template(string? existingProviderArn = null) =>
        Amazon.CDK.Assertions.Template.FromStack(new DeploymentIdentityStack(
            SynthesizedStack.NewApp(),
            "ReliableOrders-DeploymentIdentity",
            Repository,
            existingProviderArn,
            new StackProps { Env = SynthesizedStack.TestEnvironment }));
}
