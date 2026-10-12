namespace Nitrogen.Cli;

/// <summary>The arguments of <c>nitrogen parse</c> and <c>nitrogen watch</c>.</summary>
internal sealed record CliOptions(string Command, IReadOnlyList<string> Grammars, string Start, bool Tree, IReadOnlyList<string> Samples, bool Bind = false, string? Inputs = null)
{
    public const string Usage = """
        usage: nitrogen parse --grammar <file|dir> [--grammar …] --start Module.Rule [--tree] [--bind] <sample>…
               nitrogen parse [--tree] <signed-sample>…
               nitrogen check <file|directory>… | --inputs <manifest.json>
               nitrogen watch (the same arguments: recompiles and re-parses on every grammar save)
               nitrogen lsp   (a language server on stdin/stdout: .ngr and workspace languages)
        """;

    /// <summary>The options, or null with <paramref name="error"/> saying why the arguments are unusable.</summary>
    public static CliOptions? Parse(IReadOnlyList<string> args, out string error)
    {
        error = "";
        if (args.Count == 0 || args[0] is not ("parse" or "watch" or "check"))
        {
            error = args.Count == 0 ? "no command" : $"unknown command '{args[0]}'";
            return null;
        }

        var grammars = new List<string>();
        var samples = new List<string>();
        string? start = null;
        string? inputs = null;
        bool tree = false;
        bool bind = false;
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--grammar" or "-g" or "--start" or "-s" or "--inputs":
                    if (i + 1 >= args.Count)
                    {
                        error = $"{arg} needs a value";
                        return null;
                    }
                    if (arg == "--inputs") inputs = args[++i];
                    else if (arg is "--start" or "-s") start = args[++i];
                    else grammars.Add(args[++i]);
                    break;
                case "--tree" or "-t":
                    tree = true;
                    break;
                case "--bind" or "-b":
                    bind = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        error = $"unknown option '{arg}'";
                        return null;
                    }
                    samples.Add(arg);
                    break;
            }
        }

        if (inputs is not null && (args[0] != "check" || samples.Count > 0)) error = "--inputs is only valid for check without document arguments";
        else if (args[0] == "check" && (grammars.Count > 0 || start is not null || tree || bind)) error = "check selects languages from signatures/configuration";
        else if (args[0] == "check" || args[0] == "parse" && grammars.Count == 0 && start is null && samples.Count > 0)
            return new(args[0], grammars, "", tree, samples, bind, inputs);
        else if (grammars.Count == 0) error = "no --grammar";
        else if (start is null) error = "no --start";
        return error.Length == 0 ? new CliOptions(args[0], grammars, start!, tree, samples, bind, inputs) : null;
    }
}
