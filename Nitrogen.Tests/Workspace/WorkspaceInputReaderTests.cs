using System.Diagnostics;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class WorkspaceInputReaderTests
{
    [Fact]
    public async Task Zero_length_entries_do_not_block_discovery_on_a_unix_fifo()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("nitrogen-reader-").FullName;
        string pipe = Path.Combine(root, "pipe");
        Task<InputSnapshot>? reading = null;
        try
        {
            var start = new ProcessStartInfo("mkfifo") { UseShellExecute = false };
            start.ArgumentList.Add(pipe);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            string empty = Path.Combine(root, "Empty.cs");
            File.WriteAllText(empty, "");
            string grammar = Path.Combine(root, "Calc.syntax");
            File.WriteAllText(grammar, "#!nitrogen grammar\nlanguage Calc { start M.R; }");
            reading = Task.Run(() => WorkspaceInputReader.Read(root));
            var snapshot = await reading.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("", snapshot.Files[empty].Text);
            Assert.StartsWith("#!nitrogen grammar", snapshot.Files[grammar].Text);
        }
        finally
        {
            // Release the old reader on failure so the regression fails instead of hanging the suite.
            if (reading is { IsCompleted: false })
            {
                await Task.Run(() => File.WriteAllText(pipe, "")).WaitAsync(TimeSpan.FromSeconds(5));
                await reading.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Directory.Delete(root, true);
        }
    }
}
