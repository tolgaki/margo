using System.Text;
using System.Text.Json.Nodes;
using Margo.Cloud.Core;

namespace Margo.Cloud.Provisioner;

/// <summary>
/// The provisioner's settings from its environment. Python: <c>load_config</c>, the template read in
/// <c>run</c> and the <c>os.environ[...]</c> lookups in <c>provision/azure.py</c>.
/// </summary>
public static class ProvisionerConfig
{
    /// <summary>Static values from the deployment (<c>main.bicep</c> <c>provisionerConfig</c>), as JSON.</summary>
    public const string ConfigVariable = "MARGO_PROVISIONER_CONFIG";

    /// <summary>Path of the instance ARM template; <see cref="DefaultTemplatePath"/> when unset.</summary>
    public const string TemplateVariable = "MARGO_INSTANCE_TEMPLATE";

    public const string SubscriptionVariable = "AZURE_SUBSCRIPTION_ID";
    public const string ResourceGroupVariable = "AZURE_RESOURCE_GROUP";
    public const string RoutesUrlVariable = "MARGO_ROUTES_URL";
    public const string QueueUrlVariable = "MARGO_PROVISION_QUEUE_URL";

    /// <summary>The managed identity to sign in with; <c>DefaultAzureCredential</c> when unset or empty.</summary>
    public const string ClientIdVariable = "AZURE_CLIENT_ID";

    /// <summary>The file name of the template copied next to the binary at build time.</summary>
    public const string TemplateFileName = "instance.json";

    /// <summary>Configuration values set after the image is built, and the variable that overrides
    /// each one when it is set and not empty, in Python's order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Overrides { get; } =
    [
        new("image", "MARGO_INSTANCE_IMAGE"),
        new("storageRoleDefinitionId", "MARGO_STORAGE_ROLE_ID"),
        new("pullRoleDefinitionId", "MARGO_PULL_ROLE_ID"),
        new("modelRoleDefinitionId", "MARGO_MODEL_ROLE_ID"),
        new("speechRoleDefinitionId", "MARGO_SPEECH_ROLE_ID"),
        new("blueprintClientId", "MARGO_BLUEPRINT_CLIENT_ID"),
    ];

    /// <summary><c>autopilot/infra/instance.json</c> as copied next to the binary by the build.
    /// Python read <c>/app/autopilot/infra/instance.json</c> in its image.</summary>
    public static string DefaultTemplatePath => Path.Combine(AppContext.BaseDirectory, TemplateFileName);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Static values from the deployment, plus the ones set after the image is built: the JSON in
    /// <see cref="ConfigVariable"/>, then each <see cref="Overrides"/> variable that is set and not empty.
    /// </summary>
    /// <returns>The parsed JSON, whatever it is, as Python returns it. Only an override needs an object.</returns>
    /// <exception cref="KeyNotFoundException"><see cref="ConfigVariable"/> is not set (Python: KeyError).</exception>
    /// <exception cref="System.Text.Json.JsonException">It is not JSON (Python: JSONDecodeError).</exception>
    /// <exception cref="InvalidOperationException">An override is set but the configuration is not a
    /// JSON object (Python: TypeError on item assignment).</exception>
    public static JsonNode? Load(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var config = PythonJson.Loads(Require(environment, ConfigVariable));
        foreach (var (name, variable) in Overrides)
        {
            var value = environment(variable);
            if (!string.IsNullOrEmpty(value))
            {
                if (config is not JsonObject values)
                {
                    throw new InvalidOperationException("provisioner configuration is not a JSON object");
                }

                values[name] = value;
            }
        }

        return config;
    }

    /// <summary>The instance template: the file named by <see cref="TemplateVariable"/> when it is set
    /// (even empty, as Python's <c>os.getenv(name, default)</c>), otherwise <see cref="DefaultTemplatePath"/>,
    /// read as strict UTF-8 and parsed as Python's <c>json.loads</c>.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="System.Text.Json.JsonException">It is not JSON.</exception>
    public static JsonNode? LoadTemplate(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var path = environment(TemplateVariable) ?? DefaultTemplatePath;
        return PythonJson.Loads(StrictUtf8.GetString(File.ReadAllBytes(path)));
    }

    /// <summary>Python's <c>os.environ[name]</c>: an empty value is a value; a missing one is an error.</summary>
    /// <exception cref="KeyNotFoundException">The variable is not set (Python: KeyError).</exception>
    public static string Require(Func<string, string?> environment, string name)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment(name) ?? throw new KeyNotFoundException(name);
    }
}
