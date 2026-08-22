using Amazon.CDK;
using ReliableOrders.Cdk.Configuration;
using ReliableOrders.Cdk.Stacks;

// The environment is a context value rather than an environment variable, so the configuration that
// was deployed is visible in cdk.json and in the command that deployed it. An unknown name fails
// synthesis in EnvironmentConfig rather than here.
var app = NagPolicy.Apply(new App());
var config = EnvironmentConfig.ForEnvironment(EnvironmentName(app));

// Account and Region come from the credentials the CLI resolved, which keeps account IDs out of
// source. Both are demanded rather than passed through. A null leaves the stack environment-agnostic,
// which cannot look anything up at synthesis time and deploys into whichever account is supplied
// later, so a synth without credentials has to say which environment it is synthesising for.
_ = new ReliableOrdersStack(
    app,
    $"ReliableOrders-{config.EnvironmentName}",
    config,
    FunctionAsset.FromPublishOutput(RepositoryRoot()),
    new StackProps
    {
        Description = $"Reliable orders worker, {config.EnvironmentName} environment.",
        Env = new Amazon.CDK.Environment
        {
            Account = Required("CDK_DEFAULT_ACCOUNT"),
            Region = Required("CDK_DEFAULT_REGION"),
        },
    });

// Account-level and deployed once by hand, so it carries no environment in its name. The CLI is
// given a stack name on every deployment for the same reason: an app holding more than one stack
// deploys nothing without being told which, and the one that decides who may deploy is not a stack a
// workflow should be able to name.
_ = new DeploymentIdentityStack(
    app,
    "ReliableOrders-DeploymentIdentity",
    GitHubRepository.Parse(RequiredContext(app, "githubRepository")),
    OptionalContext(app, "githubOidcProviderArn"),
    new StackProps
    {
        Description = "The roles GitHub Actions assumes to deploy, and the trust that admits them.",
        Env = new Amazon.CDK.Environment
        {
            Account = Required("CDK_DEFAULT_ACCOUNT"),
            Region = Required("CDK_DEFAULT_REGION"),
        },
    });

app.Synth();

// Matched as an object rather than cast with as. A key set to anything but a string — cdk deploy -c
// environment, with no value, sets it to true — would otherwise coalesce to the development
// configuration and deploy development sizing under another environment's name.
static string EnvironmentName(App app)
{
    var value = app.Node.TryGetContext("environment");

    return value switch
    {
        null => EnvironmentConfig.Development.EnvironmentName,
        string name => name,
        _ => throw new InvalidOperationException(
            $"The environment context key is set to '{value}', which is not a name. "
            + "Pass it as -c environment=<name>."),
    };
}

// Context is read the way the environment name above is, and fails the same way. A key set to
// anything but a string is a key somebody meant to set — cdk deploy -c githubRepository, with no
// value, sets it to true — and coalescing that to the default would synthesise a trust policy naming
// a repository nobody asked for.
static string RequiredContext(App app, string key) =>
    OptionalContext(app, key)
    ?? throw new InvalidOperationException(
        $"No {key} is set. Add it to cdk.json, or pass -c {key}=<value>.");

static string? OptionalContext(App app, string key)
{
    var value = app.Node.TryGetContext(key);

    return value switch
    {
        null => null,
        string text when !string.IsNullOrWhiteSpace(text) => text,
        _ => throw new InvalidOperationException(
            $"The {key} context key is set to '{value}', which is not a value. "
            + $"Pass it as -c {key}=<value>."),
    };
}

// The publish output is found relative to the repository, so the repository has to be found first.
// Walking up for the solution file rather than counting directories up from here, because the number
// is only right when the CLI is run from the directory holding cdk.json — cdk -a, or dotnet run
// --project from the root, would resolve somewhere outside the checkout and report a path nobody
// recognises.
static string RepositoryRoot()
{
    const string marker = "ReliableOrders.slnx";

    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

    for (; directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, marker)))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException(
        $"No {marker} in '{Directory.GetCurrentDirectory()}' or any directory above it, so the "
        + "repository root cannot be found. Run this from inside the checkout.");
}

static string Required(string variable) =>
    System.Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException(
            $"{variable} is not set. The CDK CLI sets it from the credentials it resolved, so run "
            + "this through cdk with credentials configured, or set it explicitly to synthesise for "
            + "a named account and Region.");
