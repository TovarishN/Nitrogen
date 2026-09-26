namespace Nitrogen.Workspace;

/// <summary>
/// A grammar diagnostic (an NGR code) at a grammar file's line and column, or a C# error in the
/// generated code (only a generator bug produces those). Lines and columns are 1-based.
/// </summary>
public sealed record WorkspaceDiagnostic(string Path, int Line, int Column, string Code, string Message, bool IsError)
{
    public override string ToString() => $"{Path}({Line},{Column}): {(IsError ? "error" : "warning")} {Code}: {Message}";
}
