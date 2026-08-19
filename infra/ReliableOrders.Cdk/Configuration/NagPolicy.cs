using Amazon.CDK;
using Cdklabs.CdkNag;
using Constructs;

namespace ReliableOrders.Cdk.Configuration;

/// <summary>
/// The rule pack every synthesis of this app is checked against.
/// </summary>
/// <remarks>
/// <para>
/// Registered on the app rather than run as a separate step, so `cdk synth` and `cdk deploy` are
/// checked as well as the test suite. A rule engine that only runs where the tests run reports on the
/// stack the tests build, and it is the deployed one that matters.
/// </para>
/// <para>
/// cdk-nag 3 packs are policy validation plugins rather than aspects. The difference is not
/// cosmetic and every guide still shows the older form: <c>Aspects.Of(app).Add(pack)</c> compiles,
/// runs, and reports nothing at all, which is the failure that looks most like a clean stack.
/// </para>
/// <para>
/// Suppressions live on the resources they cover rather than here. A list of exceptions kept away
/// from the code they excuse is a list nobody rereads when the reason expires.
/// </para>
/// </remarks>
public static class NagPolicy
{
    /// <summary>
    /// Checks everything in <paramref name="app"/> against the AWS Solutions rules.
    /// </summary>
    /// <param name="app">The application to validate at synthesis.</param>
    /// <returns>The same application, so a composition root can chain this onto its construction.</returns>
    /// <remarks>
    /// Verbose, because the message is read by whoever the failure stops, and the rule identifier
    /// alone sends them to a rules table to find out what it means.
    /// </remarks>
    public static App Apply(App app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Validations.Of(app).AddPlugins(new AwsSolutionsChecks(app, new NagPackProps { Verbose = true }));

        return app;
    }

    /// <summary>
    /// Accepts one rule's finding for a construct and everything beneath it.
    /// </summary>
    /// <param name="scope">The construct the finding is raised against.</param>
    /// <param name="ruleId">The rule, as the validation report names it, such as AwsSolutions-DDB3.</param>
    /// <param name="reason">Why the finding is accepted here. It is read by whoever audits this later.</param>
    /// <remarks>
    /// <para>
    /// cdk-nag 3 has no NagSuppressions of its own. Suppression is the CDK's own acknowledgement
    /// mechanism, which records the rule against the construct's metadata for the pack to read back.
    /// </para>
    /// <para>
    /// <paramref name="ruleId"/> is the rule exactly as the validation report names it, and nothing is
    /// prefixed onto it here. The CDK qualifies a bare identifier with its own annotation prefix,
    /// which the pack strips before comparing, while a granular identifier such as
    /// <c>AwsSolutions-IAM5[Resource::*]</c> already contains the delimiter and is passed through
    /// untouched — so both arrive as the report's own name. Anything else acknowledges nothing, and
    /// the only symptom is a finding that will not go away.
    /// </para>
    /// <para>
    /// Scoped to a construct rather than applied to the app. An acknowledgement covers the scope it is
    /// declared on and everything beneath it, so declaring these at the root would accept the same
    /// finding on resources nobody has written yet.
    /// </para>
    /// </remarks>
    public static void Accept(IConstruct scope, string ruleId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Validations.Of(scope).Acknowledge(new Acknowledgment { Id = ruleId, Reason = reason });
    }
}
