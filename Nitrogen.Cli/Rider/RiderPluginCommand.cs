namespace Nitrogen.Cli;

internal static class RiderPluginCommand
{
    public static Task<int> RunAsync(RiderPluginRequest request, TextWriter output, CancellationToken cancel)
    {
        try
        {
            RiderPluginRenderer.Render(request, request.OutputDirectory, cancel);
            output.WriteLine($"generated Rider plugin '{request.Model.PluginId}' at {request.OutputDirectory}");
            return Task.FromResult(0);
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("error: Rider plugin generation cancelled");
            return Task.FromResult(1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            output.WriteLine($"error: Rider plugin generation failed: {exception.Message}");
            return Task.FromResult(1);
        }
    }
}
