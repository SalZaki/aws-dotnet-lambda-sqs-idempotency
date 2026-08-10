using Amazon.CDK;
using ReliableOrders.Cdk.Configuration;
using ReliableOrders.Cdk.Stacks;

// The environment is a context value rather than an environment variable, so the configuration that
// was deployed is visible in cdk.json and in the command that deployed it. An unknown name fails
// synthesis in EnvironmentConfig rather than here.
var app = new App();
var config = EnvironmentConfig.ForEnvironment(EnvironmentName(app));

// Account and Region come from the credentials the CLI resolved, which keeps account IDs out of
// source. Both are demanded rather than passed through. A null leaves the stack environment-agnostic,
// which cannot look anything up at synthesis time and deploys into whichever account is supplied
// later, so a synth without credentials has to say which environment it is synthesising for.
_ = new ReliableOrdersStack(
    app,
    $"ReliableOrders-{config.EnvironmentName}",
    config,
    new StackProps
    {
        Description = $"Reliable orders worker, {config.EnvironmentName} environment.",
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

static string Required(string variable) =>
    System.Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException(
            $"{variable} is not set. The CDK CLI sets it from the credentials it resolved, so run "
            + "this through cdk with credentials configured, or set it explicitly to synthesise for "
            + "a named account and Region.");
