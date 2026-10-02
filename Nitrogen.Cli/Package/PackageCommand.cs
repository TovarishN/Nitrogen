namespace Nitrogen.Cli;

/// <summary><c>nitrogen package</c>: generate, build, and collect installable editor plugins (spec: installable language plugins).</summary>
internal static class PackageCommand
{
    /// <returns>0 built, 1 a build step failed, 2 a required tool is missing.</returns>
    public static async Task<int> RunAsync(PackageRequest request, TextWriter output, CancellationToken cancel, Tools? tools = null)
    {
        tools ??= new Tools();
        string? npm = request.VsCode ? tools.Find("npm") : null;
        string? gradle = request.Rider ? tools.Find("gradle") : null;
        if (request.VsCode && npm is null) return Missing(output, "'npm' (Node.js) is needed to build the VS Code extension");
        if (request.Rider && gradle is null) return Missing(output, "'gradle' is needed to build the Rider plugin");
        if (request.Rider && tools.Java() is null) return Missing(output, "a Java runtime (JAVA_HOME or java on PATH, JDK 25 for Rider 2026.2) is needed to build the Rider plugin");

        var model = request.Model;
        string build = Path.Combine(request.OutputDirectory, ".build");
        try
        {
            if (request.VsCode)
            {
                string project = Path.Combine(build, "vscode");
                VsCodeRenderer.Render(new VsCodeRequest(model, project, request.ServerDirectory), cancel);
                string vsix = Path.Combine(request.OutputDirectory, $"{model.PluginId}-{model.Version}.vsix");
                if (await tools.RunAsync(npm!, ["ci"], project, output, cancel) is not 0 and var ci) return Failed(output, "npm ci", ci);
                if (await tools.RunAsync(npm!, ["run", "compile"], project, output, cancel) is not 0 and var compile) return Failed(output, "npm run compile", compile);
                if (await tools.RunAsync(npm!, ["run", "package", "--", "--out", vsix], project, output, cancel) is not 0 and var pack) return Failed(output, "vsce package", pack);
                output.WriteLine($"built {vsix}");
            }
            if (request.Rider)
            {
                string project = Path.Combine(build, "rider");
                RiderPluginRenderer.Render(new RiderPluginRequest(model, project, "nitrogen", [], request.ServerDirectory), project, cancel);
                if (await tools.RunAsync(gradle!, ["buildPlugin", "--console=plain"], project, output, cancel) is not 0 and var code) return Failed(output, "gradle buildPlugin", code);
                string built = Directory.GetFiles(Path.Combine(project, "build", "distributions"), "*.zip").Single();
                string zip = Path.Combine(request.OutputDirectory, $"{model.PluginId}-{model.Version}-rider.zip");
                File.Copy(built, zip, overwrite: true);
                output.WriteLine($"built {zip}");
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            output.WriteLine($"error: packaging failed: {exception.Message}");
            return 1;
        }
    }

    static int Missing(TextWriter output, string message)
    {
        output.WriteLine($"error: {message}");
        return 2;
    }

    static int Failed(TextWriter output, string step, int code)
    {
        output.WriteLine($"error: {step} exited with {code}");
        return 1;
    }
}
