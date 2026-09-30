namespace Nitrogen.Cli;

internal static class VsCodeCommand
{
    public static Task<int> RunAsync(VsCodeRequest request, TextWriter output, CancellationToken cancel)
    {
        try
        {
            VsCodeRenderer.Render(request, cancel);
            output.WriteLine($"generated VS Code extension '{VsCodeRenderer.ExtensionName(request.Model)}' at {request.OutputDirectory}");
            return Task.FromResult(0);
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("error: VS Code extension generation cancelled");
            return Task.FromResult(1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            output.WriteLine($"error: VS Code extension generation failed: {exception.Message}");
            return Task.FromResult(1);
        }
    }
}
