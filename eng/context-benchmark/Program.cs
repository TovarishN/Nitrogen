using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nitrogen;
using S = ContextBenchmark.CalcSyntax;
#if SCOPED_CONTEXTS
using T = ContextBenchmark.ScopedSyntax;
#endif

var measurements = new List<object>();
var calc = new LanguageBuilder().Add(S.CalcModule.Instance).Build();
string calcText = string.Concat(Enumerable.Range(0, 30).Select(i => $"x{i} = a*(b+{i}) - f(c, 2);\n"));
Measure("Calc/default", calc, S.CalcModule.Program, calcText, 2000);
MeasureIncremental("Calc/default/incremental",calc,S.CalcModule.Program,calcText,0,'y',2000);
#if CSHARP_CONSUMER
var fourth = CSNitrogen.Syntax.CSharp4Composition.CreateBuilder().Build();
string csharp = string.Concat(Enumerable.Range(0, 15).Select(i => $"class C{i}<T>{{public T M(T value=default(T)){{return value;}} public void N(){{M(value:default(T));}}}}\n"));
Measure("CSharp4/default", fourth, CSNitrogen.Syntax.CSharp4Composition.Start, csharp, 500);
MeasureIncremental("CSharp4/default/incremental",fourth,CSNitrogen.Syntax.CSharp4Composition.Start,csharp,6,'D',2000);
#endif
#if SCOPED_CONTEXTS
var scoped = new LanguageBuilder().Add(T.FlagsModule.Instance).Add(T.GrammarContextModule.Instance).Build();
string scopedText=string.Concat(Enumerable.Repeat("a x n x ",60));
Measure("Scoped/active",scoped,T.GrammarContextModule.File,scopedText,2000);
MeasureIncremental("Scoped/active/incremental",scoped,T.GrammarContextModule.File,scopedText,0,'n',2000);
#endif
var memoEntry = typeof(Language).Assembly.GetType("Nitrogen.MemoTable+Entry", true)!;
var reuseEntry = typeof(Language).Assembly.GetType("Nitrogen.ReuseRecord", true)!;
Console.WriteLine(JsonSerializer.Serialize(new { runtime = Environment.Version.ToString(), memoEntryBytes = Marshal.SizeOf(memoEntry), reuseEntryBytes = Marshal.SizeOf(reuseEntry), measurements }));

void Measure(string name, Language language, Rule start, string text, int iterations)
{
    for (int i = 0; i < 1000; i++) { using var warm = language.Parse(text, start); Check(warm); }
    var milliseconds = new double[7];
    var allocations = new long[7];
    ParseStats stats = default;
    for (int round = 0; round < 7; round++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) { using var result = language.Parse(text, start); Check(result); stats = result.Stats; }
        clock.Stop();
        allocations[round] = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        milliseconds[round] = clock.Elapsed.TotalMilliseconds / iterations;
    }
    measurements.Add(new { name, characters = text.Length, iterations, milliseconds, allocatedBytesPerParse = allocations, nodes = stats.TreeNodes, memoHits = stats.MemoHits, memoMisses = stats.MemoMisses });
}
void MeasureIncremental(string name,Language language,Rule start,string text,int offset,char replacement,int iterations)
{
    string changed=text[..offset]+replacement+text[(offset+1)..];
    var changes=new TextChange[]{new(offset,1,1)};
    var milliseconds=new double[7];var allocations=new long[7];ParseStats stats=default;
    for(int round=0;round<7;round++)
    {
        var current=language.ParseIncremental(text,start);Check(current);
        try
        {
            for(int i=0;i<1000;i++){var next=language.Reparse(current,i%2==0?changed:text,changes);Check(next);current.Dispose();current=next;}
            GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long before=GC.GetAllocatedBytesForCurrentThread();var clock=Stopwatch.StartNew();
            for(int i=0;i<iterations;i++){var next=language.Reparse(current,i%2==0?changed:text,changes);Check(next);stats=next.Stats;current.Dispose();current=next;}
            clock.Stop();allocations[round]=(GC.GetAllocatedBytesForCurrentThread()-before)/iterations;milliseconds[round]=clock.Elapsed.TotalMilliseconds/iterations;
        }
        finally{current.Dispose();}
    }
    measurements.Add(new{name,characters=text.Length,iterations,milliseconds,allocatedBytesPerParse=allocations,nodes=stats.TreeNodes,memoHits=stats.MemoHits,memoMisses=stats.MemoMisses,reusedNodes=stats.ReusedNodes});
}
static void Check(ParseResult result)
{
    if (!result.Success || result.HasErrors) throw new InvalidOperationException("Benchmark input must parse without errors.");
}
