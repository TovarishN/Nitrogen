using Nitrogen.Cli;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
if (args is ["lsp"])
    return await LspCommand.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error, cancel.Token);
if (args is ["lsp", "--config", var config])
{
    if (LspCommand.ConfigRoot(config, out string error) is not { } root)
    {
        Console.Error.WriteLine($"nitrogen lsp: {error}");
        return 2;
    }
    return await LspCommand.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error, cancel.Token, root);
}
return await NitrogenCli.RunAsync(args, Console.Out, cancel.Token);
