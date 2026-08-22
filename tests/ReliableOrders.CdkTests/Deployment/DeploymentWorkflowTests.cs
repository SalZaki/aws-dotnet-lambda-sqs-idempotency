using System.Text.RegularExpressions;
using ReliableOrders.Cdk.Configuration;
using ReliableOrders.Cdk.Stacks;

namespace ReliableOrders.CdkTests.Deployment;

/// <summary>
/// The two workflows that hold AWS credentials, read as files.
/// </summary>
/// <remarks>
/// <para>
/// Not a parse of the workflow — GitHub's schema is the authority on what these files mean, and a
/// second reader of it here would be a second thing to keep right. What each case asserts is a
/// string whose absence is a security property lost, which is a claim plain text can carry.
/// </para>
/// <para>
/// They are cheap because the failure they guard against is silent. A deployment workflow that grew
/// a <c>pull_request</c> trigger would work, would be reviewed as a convenience, and would hand the
/// deployment role to whatever a fork proposed.
/// </para>
/// </remarks>
public sealed partial class DeploymentWorkflowTests
{
    /// <summary>
    /// Neither deployment workflow can be started by a pull request.
    /// </summary>
    /// <remarks>
    /// The whole string, anywhere in the file. A trigger is the obvious way to reintroduce this, and a
    /// condition mentioning the event is the second — both would show up here, and neither belongs in
    /// a file that assumes a role.
    /// </remarks>
    [Theory]
    [InlineData("deploy-dev.yml")]
    [InlineData("release.yml")]
    [InlineData("e2e.yml")]
    public void A_deployment_workflow_is_never_started_by_a_pull_request(string file)
    {
        Assert.DoesNotContain("pull_request", Workflow(file), StringComparison.Ordinal);
    }

    /// <summary>
    /// Each deploying job names the environment whose subject its role trusts.
    /// </summary>
    /// <remarks>
    /// Dropping the line would not be caught by a review that read the trust policy as the whole
    /// control: without an environment the token carries a subject built from the ref instead, so what
    /// this really asserts is that the two halves of one decision still agree.
    /// </remarks>
    [Theory]
    [InlineData("deploy-dev.yml", DeploymentIdentityStack.DevelopmentEnvironmentName)]
    [InlineData("release.yml", DeploymentIdentityStack.ReleaseEnvironmentName)]
    [InlineData("e2e.yml", DeploymentIdentityStack.EndToEndEnvironmentName)]
    public void A_deploying_job_runs_in_the_environment_its_role_trusts(string file, string environmentName)
    {
        var workflow = Workflow(file);

        Assert.Contains($"environment: {environmentName}", workflow, StringComparison.Ordinal);
        Assert.Contains("id-token: write", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The development deployment refuses a run that came from a fork.
    /// </summary>
    /// <remarks>
    /// It is triggered by a completed run of ci, and ci runs on pull requests. A completed run raises
    /// the event in this repository's context whoever opened the pull request, so this comparison is
    /// the clause that a fork cannot satisfy — the branch name and the event are both under its
    /// author's control.
    /// </remarks>
    [Fact]
    public void The_development_deployment_refuses_a_run_from_a_fork()
    {
        var workflow = Workflow("deploy-dev.yml");

        Assert.Contains(
            "github.event.workflow_run.head_repository.full_name == github.repository",
            workflow,
            StringComparison.Ordinal);

        Assert.Contains("github.event.workflow_run.conclusion == 'success'", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The end-to-end run destroys its stack whatever became of the tests.
    /// </summary>
    /// <remarks>
    /// always(), not success() and not failure(). A stack outlives a cancelled run as readily as a
    /// failed one and bills the same — two queues, two tables and a log group — and the run that
    /// leaves one behind is exactly the run nobody goes back to read.
    /// </remarks>
    [Fact]
    public void The_end_to_end_run_destroys_what_it_deployed()
    {
        var workflow = Workflow("e2e.yml");

        Assert.Contains("if: ${{ always() }}", workflow, StringComparison.Ordinal);
        Assert.Contains("npx cdk destroy", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ephemeral stack is named for the run, through the environment rather than the stack alone.
    /// </summary>
    /// <remarks>
    /// The queues take their names from the environment, so two runs sharing one would collide on the
    /// source queue however their stacks were called — and a run colliding with `dev` would send test
    /// messages to the queue people are using. <c>EnvironmentConfig.Ephemeral</c> is where that is
    /// argued; this is what stops the workflow passing the run to only half of it.
    /// </remarks>
    [Fact]
    public void The_ephemeral_stack_is_named_for_the_run()
    {
        var workflow = Workflow("e2e.yml");

        Assert.Contains($"ENVIRONMENT: {EnvironmentConfig.EphemeralPrefix}${{{{ github.run_id }}}}", workflow, StringComparison.Ordinal);
        Assert.Contains("-c environment=\"$ENVIRONMENT\"", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every action every workflow uses is pinned to a commit, not to a tag.
    /// </summary>
    /// <remarks>
    /// Across all the workflows rather than the deployment ones alone. A tag is a moving reference, so
    /// a pinned action is the difference between reviewing what runs and trusting whoever can move
    /// the tag — and an action in the gate can reach the pull request that the deployment then
    /// deploys.
    /// </remarks>
    [Fact]
    public void Every_action_is_pinned_to_a_commit()
    {
        var directory = Path.Combine(RepositoryFiles.Root, ".github", "workflows");
        var unpinned = new List<string>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.yml").Order(StringComparer.Ordinal))
        {
            unpinned.AddRange(
                from Match match in ActionReference().Matches(File.ReadAllText(path))
                let reference = match.Groups["reference"].Value
                where !CommitSha().IsMatch(reference)
                select $"{Path.GetFileName(path)}: {match.Groups["action"].Value}@{reference}");
        }

        Assert.True(
            unpinned.Count == 0,
            $"These actions are not pinned to a commit: {string.Join(", ", unpinned)}. A tag can be "
            + "moved by whoever owns the action, and what runs then is not what was reviewed.");
    }

    /// <summary>
    /// The outputs the deployment checks for are the outputs the stack publishes.
    /// </summary>
    /// <remarks>
    /// The script holds the list because the check is a script rather than a program that could
    /// reference the stack. This is what stops it becoming a stale copy: an output renamed in the
    /// stack and not there leaves the check passing over a name nothing produces any more, which is
    /// the check reporting on nothing.
    /// </remarks>
    [Fact]
    public void The_deployment_checks_every_output_the_stack_publishes()
    {
        var checkedNames = ExpectedOutput().Matches(RepositoryFiles.Read(OutputCheck))
            .Select(match => match.Groups["name"].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var published = SynthesizedStack.From(EnvironmentConfig.Development)
            .FindOutputs("*")
            .Keys
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(checkedNames);
        Assert.Equal(published, checkedNames);
    }

    /// <summary>
    /// Both deployments run that check, rather than one of them.
    /// </summary>
    /// <remarks>
    /// The release deploys the same stack behind an approval, so a release that skipped the check
    /// would be the one path where a stack deploying without an output goes unreported — and the
    /// case above would still pass, because it reads the script rather than the workflows.
    /// </remarks>
    [Theory]
    [InlineData("deploy-dev.yml")]
    [InlineData("release.yml")]
    [InlineData("e2e.yml")]
    public void A_deployment_checks_the_outputs_it_produced(string file)
    {
        var workflow = Workflow(file);

        Assert.Contains("--outputs-file", workflow, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(OutputCheck), workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Neither deployment can be cancelled out of the queue by a run that deploys nothing.
    /// </summary>
    /// <remarks>
    /// A workflow-level concurrency group is entered by the run before any condition on a job is
    /// read, and every completed run of ci raises deploy-dev — pull requests included. GitHub keeps
    /// one pending run per group and cancels the one it replaces, so a group declared at the top of
    /// these files would let a no-op run cancel a queued deployment of main.
    /// </remarks>
    [Theory]
    [InlineData("deploy-dev.yml", "group: deploy-reliable-orders-dev")]
    [InlineData("release.yml", "group: deploy-reliable-orders-dev")]
    [InlineData("e2e.yml", "group: e2e")]
    public void A_deployment_holds_its_concurrency_group_from_the_job(string file, string group)
    {
        var workflow = Workflow(file);

        Assert.Contains("    concurrency:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("\nconcurrency:", workflow, StringComparison.Ordinal);
        Assert.Contains(group, workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Neither workflow starts in a repository that has no account to deploy to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The variable is set by the setup script and no deployment can work without it, so its absence
    /// is the honest test for "not configured yet". Without the clause a fork of this repository
    /// fails on every push to its own default branch, at the credentials step, over an account it was
    /// never going to have — and so did this repository on the first push after the deployment story
    /// merged.
    /// </para>
    /// <para>
    /// It has to be a variable rather than the role ARN it stands in for: <c>vars</c> is readable in
    /// a job condition and <c>secrets</c> is not.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("deploy-dev.yml", "      vars.AWS_REGION != '' &&")]
    [InlineData("release.yml", "    if: ${{ vars.AWS_REGION != '' }}")]
    [InlineData("e2e.yml", "    if: ${{ vars.AWS_REGION != '' }}")]
    public void A_deployment_waits_for_an_account_to_deploy_to(string file, string clause)
    {
        // The clause as the condition writes it, rather than the variable's name anywhere in the
        // file. A search for the name alone would pass on `||` in place of `&&`, which is the change
        // that reads as a fix and removes the property this case is named for.
        Assert.Contains(clause, Workflow(file), StringComparison.Ordinal);
    }

    /// <summary>
    /// The release survives a deployment that had no account to run against.
    /// </summary>
    /// <remarks>
    /// A release is a fact about this repository and a deployment is one about an account, so a
    /// variable cleared while an account is rotated should cost the second and not the first. It does
    /// not survive a deployment that ran and failed, which is a release nobody should be reading
    /// notes for.
    /// </remarks>
    [Fact]
    public void The_release_is_published_even_where_there_was_nothing_to_deploy_to()
    {
        var workflow = Workflow("release.yml");

        Assert.Contains("needs.verify.result == 'success'", workflow, StringComparison.Ordinal);
        Assert.Contains("needs.deploy.result != 'failure'", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tag is verified whether or not there is an account to deploy to.
    /// </summary>
    /// <remarks>
    /// Whether a tag is signed is a question about this repository. Gating the verifying job on the
    /// account gated the release with it: a cleared variable would have skipped all three jobs and
    /// reported the run green, having verified nothing and published nothing.
    /// </remarks>
    [Fact]
    public void The_tag_is_verified_without_an_account()
    {
        var workflow = Workflow("release.yml");

        Assert.Contains("    if: startsWith(github.ref, 'refs/tags/v')", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The release deploys the commit whose signature was verified, not the tag that named it.
    /// </summary>
    /// <remarks>
    /// The approval gate sits between the two jobs, and a tag can be moved while a reviewer is
    /// deciding. A checkout resolving the tag a second time would report on one commit and deploy
    /// whichever the tag pointed at by then.
    /// </remarks>
    [Fact]
    public void The_release_deploys_the_commit_it_verified()
    {
        var workflow = Workflow("release.yml");

        Assert.Contains("ref: ${{ needs.verify.outputs.commit }}", workflow, StringComparison.Ordinal);
    }

    /// <summary>Reads one workflow out of the working tree.</summary>
    private static string Workflow(string file) =>
        RepositoryFiles.Read(Path.Combine(".github", "workflows", file));

    /// <summary>An action reference, as a workflow writes one.</summary>
    [GeneratedRegex(@"uses:\s*(?<action>[\w.-]+/[\w.-]+(?:/[\w.-]+)*)@(?<reference>\S+)")]
    private static partial Regex ActionReference();

    /// <summary>What a commit looks like, against what a tag looks like.</summary>
    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitSha();

    /// <summary>The check both deployments run.</summary>
    private static readonly string OutputCheck = Path.Combine("scripts", "check-stack-outputs.py");

    /// <summary>One entry of the list that check reads.</summary>
    /// <remarks>
    /// Anchored to the quoted, indented entries of the list rather than to any name in the file, so
    /// an unrelated quoted string elsewhere in the script does not read as an output.
    /// </remarks>
    [GeneratedRegex("^ {4}\"(?<name>\\w+)\",$", RegexOptions.Multiline)]
    private static partial Regex ExpectedOutput();
}
