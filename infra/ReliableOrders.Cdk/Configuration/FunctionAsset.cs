using System.Reflection;
using System.Runtime.Versioning;
using Amazon.CDK.AWS.Lambda;

namespace ReliableOrders.Cdk.Configuration;

/// <summary>
/// Finds the published function on disk.
/// </summary>
/// <remarks>
/// <para>
/// The CDK does not build the function. A bundling step would need either Docker or a matching SDK on
/// whatever machine synthesises, and the deployment pipeline already builds and tests the solution
/// before it deploys — publishing twice would mean deploying a second binary that nothing tested.
/// </para>
/// <para>
/// The cost of not bundling is that synthesis can silently package the wrong thing, so it does not
/// silently do anything. A missing directory fails naming the publish command, and so does one that
/// is missing either of the two files the runtime cannot start without — which is what an interrupted
/// publish, or a publish to another configuration, leaves behind.
/// </para>
/// </remarks>
public static class FunctionAsset
{
    /// <summary>The project whose publish output is deployed.</summary>
    public const string ProjectDirectory = "src/ReliableOrders.Function";

    /// <summary>The assembly the handler lives in, and one of the two files the package cannot be missing.</summary>
    public const string FunctionAssembly = "ReliableOrders.Function.dll";

    /// <summary>
    /// The runtime configuration the .NET runtime loads the assembly through.
    /// </summary>
    /// <remarks>
    /// The other file the package cannot be missing, and the one a reader would not think to look
    /// for. A class library emits no runtime configuration by default, and without it the Lambda
    /// bootstrap refuses the package before the handler is resolved — reported as a missing
    /// <c>.runtimeconfig.json</c> rather than as anything about the function. The project asks for it
    /// explicitly; this is what notices if that line is ever removed.
    /// </remarks>
    public const string RuntimeConfiguration = "ReliableOrders.Function.runtimeconfig.json";

    /// <summary>The command that produces it.</summary>
    public const string PublishCommand =
        "dotnet publish src/ReliableOrders.Function -c Release";

    /// <summary>
    /// Where <c>dotnet publish</c> puts the function, relative to the repository root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The framework is read off this assembly rather than written down. Both projects take their
    /// target framework from the same <c>Directory.Build.props</c>, so they always agree — where a
    /// literal would keep pointing at the previous framework's directory after a bump, and that
    /// directory still holds the last build, which would deploy silently.
    /// </para>
    /// <para>
    /// Behind a <see cref="Lazy{T}"/> because deriving it can fail. A static initialiser that throws
    /// is wrapped in a <c>TypeInitializationException</c>, which buries the message one level below
    /// where it is read and faults every other member of this type along with it.
    /// </para>
    /// </remarks>
    public static string PublishDirectory => LazyPublishDirectory.Value;

    private static readonly Lazy<string> LazyPublishDirectory =
        new(() => $"{ProjectDirectory}/bin/Release/{TargetFramework()}/publish");

    /// <summary>
    /// Returns the published output as a Lambda asset.
    /// </summary>
    /// <param name="repositoryRoot">The directory <see cref="PublishDirectory"/> is relative to.</param>
    /// <exception cref="InvalidOperationException">
    /// The directory is absent, or is missing the handler's assembly or its runtime configuration. The
    /// message names the file and the command that produces it.
    /// </exception>
    public static Code FromPublishOutput(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var path = Path.Combine(repositoryRoot, PublishDirectory);

        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"The function has not been published. Expected '{path}'. Run {PublishCommand}.");
        }

        // Named files rather than a count of them. An interrupted publish, or one that left only the
        // dependencies behind, is not empty and would deploy a package the runtime cannot start —
        // which fails on the first message rather than at synthesis.
        Require(path, FunctionAssembly, "the deployed function would have no handler to run");

        Require(
            path,
            RuntimeConfiguration,
            "the .NET runtime would refuse the package before it resolved the handler");

        return Code.FromAsset(path);
    }

    /// <summary>
    /// Fails naming a file the package cannot be deployed without.
    /// </summary>
    /// <param name="path">The publish directory.</param>
    /// <param name="fileName">What has to be in it.</param>
    /// <param name="consequence">What its absence would cost, so the message says why it matters.</param>
    private static void Require(string path, string fileName, string consequence)
    {
        if (!File.Exists(Path.Combine(path, fileName)))
        {
            throw new InvalidOperationException(
                $"'{path}' holds no {fileName}, so {consequence}. Run {PublishCommand}.");
        }
    }

    /// <summary>
    /// The target framework moniker of the running application.
    /// </summary>
    /// <remarks>
    /// <c>TargetFrameworkAttribute</c> carries ".NETCoreApp,Version=v10.0", which is the same framework
    /// the function is built for and needs turning back into the "net10.0" that names the directory.
    /// </remarks>
    private static string TargetFramework()
    {
        var attribute = Assembly.GetExecutingAssembly().GetCustomAttribute<TargetFrameworkAttribute>()
            ?? throw new InvalidOperationException(
                "This assembly declares no target framework, so the function's publish directory "
                + "cannot be derived. Name it explicitly if this build is not from the SDK.");

        var version = attribute.FrameworkName.Split("Version=v")[^1];

        return $"net{version}";
    }
}
