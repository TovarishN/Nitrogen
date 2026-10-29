# ScopedWords

Open this directory as a workspace with the Nitrogen extension/server built from the same checkout. `nitrogen.json` registers the actual grammar and `.scopedwords` start rule. The sample uses identical `bang` payloads: inside `loud` they produce `LoudWord` nodes; inside `quiet` they produce `QuietWord` nodes.

The grammar declares a Boolean flag, tests it without producing a tree slot, and scopes a child list under an explicit true/false value. Returning from the scoped child restores the caller value. No text is rewritten and the list retains its normal typed children.

Workspace compilation, sample diagnostics and keyword navigation are checked automatically. Live VS Code/Rider behavior has not been manually verified, and this example makes no CSharp5 or semantic execution claim.
