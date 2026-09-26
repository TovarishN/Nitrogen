using Nitrogen.Cli;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
if (args is ["lsp"])
    return await LspCommand.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error, cancel.Token);
return await NitrogenCli.RunAsync(args, Console.Out, cancel.Token);
