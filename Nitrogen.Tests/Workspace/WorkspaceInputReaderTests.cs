using System.Diagnostics;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class WorkspaceInputReaderTests
{
    [Fact]
    public void A_directory_removed_after_listing_does_not_abort_remaining_discovery()
    {
        string root = Directory.CreateTempSubdirectory("nitrogen-reader-race-").FullName;
        try
        {
            string marker = Path.Combine(root, "marker.ngr");
            File.WriteAllText(marker, "syntax module Marker { }");
            string removed = Directory.CreateDirectory(Path.Combine(root, "a-removed")).FullName;
            File.WriteAllText(Path.Combine(removed, "gone.ngr"), "syntax module Gone { }");
            string retained = Directory.CreateDirectory(Path.Combine(root, "b-retained")).FullName;
            string grammar = Path.Combine(retained, "kept.ngr");
            File.WriteAllText(grammar, "syntax module Kept { }");
            using var files = WorkspaceInputReader.Enumerate(root).GetEnumerator();
            Assert.True(files.MoveNext()); // Both child directories were captured before this yield.
            Assert.Equal(marker, files.Current);
            Directory.Delete(removed, true);
            var remaining = new List<string>();
            while (files.MoveNext()) remaining.Add(files.Current);
            Assert.Equal([grammar], remaining);
        }
        finally { Directory.Delete(root, true); }
    }

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
