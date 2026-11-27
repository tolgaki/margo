using Margo.Cloud.Provisioner;

// The provisioner job. Python (removed at the cutover): provision/__main__.py.
var commands = new ProvisionerCommands(
    Environment.GetEnvironmentVariable,
    () => new AzureProvisionerServices(Environment.GetEnvironmentVariable),
    Console.Error);
return await commands.InvokeAsync(args);
