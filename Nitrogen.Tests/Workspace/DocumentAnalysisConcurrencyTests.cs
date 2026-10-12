using Nitrogen.Grammar;
using Nitrogen.Workspace;
using Xunit;

namespace Nitrogen.Tests;

public sealed class DocumentAnalysisConcurrencyTests
{
    sealed class BlockingResolver : ILanguageDefinitionResolver
    {
        readonly LanguageDefinitionResolver _inner = new();
        int _calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DefinitionResult Resolve(LanguageReference reference, string originPath, InputSnapshot inputs)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.SetResult();
                Release.Task.GetAwaiter().GetResult();
            }
            return _inner.Resolve(reference, originPath, inputs);
        }
    }

    [Fact]
    public async Task Queued_reclassification_makes_older_result_unpublishable()
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        string grammar = Path.Combine(root, "AnalysisConcurrency.syntax"), path = Path.Combine(root, "AnalysisConcurrency.txt");
        var inputs = new InputSnapshot(root, new Dictionary<string, DocumentInput> {
            [grammar] = new(grammar, "language L { start M.R; } syntax module M { syntax R = \"ok\"; }", 1) }, true);
        var resolver = new BlockingResolver();
        using var session = new DocumentAnalysisSession(resolver);
        var first = Task.Run(() => session.AnalyzeAsync(new(path, "#!nitrogen language \"./AnalysisConcurrency.syntax\"\nbad", 1), inputs, CancellationToken.None));
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var second = session.AnalyzeAsync(new(path, "plain native text", 2), inputs, CancellationToken.None);
            resolver.Release.SetResult();
            Assert.False(session.IsCurrent(await first));
            var current = await second;
            Assert.True(session.IsCurrent(current));
            Assert.Empty(current.Diagnostics);
        }
        finally { resolver.Release.TrySetResult(); }
    }
}
