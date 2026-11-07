using Margo.Cloud.Provisioner;

// The provisioner job. Python: python -m margo_autopilot.provision (provision/__main__.py).
var commands = new ProvisionerCommands(
    Environment.GetEnvironmentVariable,
    () => new AzureProvisionerServices(Environment.GetEnvironmentVariable),
    Console.Error);
return await commands.InvokeAsync(args);
