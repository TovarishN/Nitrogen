using Nitrogen.Cli;
using Xunit;

namespace Nitrogen.Tests;

/// <summary>The arguments of `nitrogen parse` / `nitrogen watch` (issue 236).</summary>
public class CliOptionsTests
{
    [Fact]
    public void Every_option_is_read()
    {
        var options = CliOptions.Parse(new[] { "watch", "--grammar", "a", "-g", "b.ngr", "--start", "M.R", "--tree", "--bind", "x.txt", "y.txt" }, out string error);
        Assert.Equal("", error);
        Assert.NotNull(options);
        Assert.Equal("watch", options.Command);
        Assert.Equal(new[] { "a", "b.ngr" }, options.Grammars);
        Assert.Equal("M.R", options.Start);
        Assert.True(options.Tree);
        Assert.True(options.Bind);
        Assert.Equal(new[] { "x.txt", "y.txt" }, options.Samples);
    }

    [Theory]
    [InlineData("", "no command")]
    [InlineData("build -g a -s M.R", "unknown command 'build'")]
    [InlineData("parse -s M.R x", "no --grammar")]
    [InlineData("parse -g a x", "no --start")]
    [InlineData("parse -g a -s M.R --verbose", "unknown option '--verbose'")]
    [InlineData("parse -g a -s", "-s needs a value")]
    public void Unusable_arguments_say_why(string args, string expected)
    {
        var options = CliOptions.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries), out string error);
        Assert.Null(options);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Bind_is_off_unless_asked() =>
        Assert.False(CliOptions.Parse(new[] { "parse", "-g", "a", "-s", "M.R" }, out _)!.Bind);
}
