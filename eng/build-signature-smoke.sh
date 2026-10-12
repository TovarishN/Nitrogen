#!/usr/bin/env bash
# Build a real consumer from the local packages, never the repository project references.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
if [[ "${NITROGEN_SMOKE_KEEP:-}" == "1" ]]; then trap 'echo "Smoke files: $work"' EXIT; else trap 'rm -rf "$work"' EXIT; fi
feed="${1:-$work/feed}"
version="${2:-0.0.0-signature-smoke.$(date +%s)}"
reference='"./Rules.syntax"'
configReference='./Rules.syntax'
if [[ "${NITROGEN_SMOKE_NAMES:-}" == "1" ]]; then reference=Rules; configReference=Rules; fi
mkdir -p "$feed"
feed="$(cd "$feed" && pwd)"
if [[ $# -eq 0 ]]; then
  for project in Nitrogen.Runtime Nitrogen.Generator Nitrogen.Cli Nitrogen.Build; do
    dotnet pack "$root/$project/$project.csproj" -c Release -p:Version="$version" -o "$feed" > "$work/pack-$project.log" 2>&1 || { cat "$work/pack-$project.log"; exit 1; }
  done
fi
unzip -p "$feed/Nitrogen.Build.$version.nupkg" Nitrogen.Build.nuspec | grep -q 'dependency id="Nitrogen.Generator"'
unzip -l "$feed/Nitrogen.Build.$version.nupkg" | grep -q 'tools/net10.0/any/nitrogen.runtimeconfig.json'
consumer="$work/native consumer"
mkdir -p "$consumer"
cat > "$work/nuget.config" <<XML
<configuration><packageSources><clear/><add key="local" value="$feed"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>
XML
cat > "$consumer/Native.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
<ItemGroup><PackageReference Include="Nitrogen.Build" Version="$version"/></ItemGroup>
</Project>
XML
printf 'public sealed class App {}\n' > "$consumer/Host.cs"
build() { dotnet build "$consumer/Native.csproj" --configfile "$work/nuget.config" --verbosity normal "$@" > "$work/build.log" 2>&1; }
expect_failure() { if build "$@"; then cat "$work/build.log"; echo 'expected Nitrogen failure'; exit 1; fi; }
build || { cat "$work/build.log"; exit 1; }
! grep -q 'Nitrogen source check' "$work/build.log"
cat > "$consumer/Rules.syntax" <<'NGR'
#!nitrogen grammar
language Rules { start Rules.Document; sources "./Checks.cs"; }
syntax module Rules {
 token Content = .+;
 syntax Document = Body:Content { check AU1001 Checks.Allowed(Body.Text) : "forbidden source" at Body; }
}
NGR
printf 'public static class Checks { public static bool Allowed(string text) => !text.Contains("forbidden"); }\n' > "$consumer/Checks.cs"
cat > "$consumer/Host.cs" <<'CS'
public sealed class App {}
/* #!nitrogen region "./Rules.syntax"
allowed
#!nitrogen end
*/
CS
if [[ "${NITROGEN_SMOKE_NAMES:-}" == "1" ]]; then
  sed 's#"./Rules.syntax"#Rules#' "$consumer/Host.cs" > "$work/Host.names"
  mv "$work/Host.names" "$consumer/Host.cs"
fi
build || { cat "$work/build.log"; exit 1; }
grep -q 'Nitrogen source check' "$work/build.log"
# Source filenames containing quotes are valid JSON inputs even though Csc forbids quoted Compile paths.
printf '#!nitrogen language %s\nallowed\n' "$reference" > "$consumer/quoted\" program.txt"
printf 'public sealed class Use { public static System.Type Module => typeof(Rules.Syntax.RulesModule); }\n' > "$consumer/Use.cs"
build || { cat "$work/build.log"; exit 1; }
rm "$consumer/quoted\" program.txt"
sed 's/^allowed$/forbidden/' "$consumer/Host.cs" > "$consumer/changed.cs"
mv "$consumer/changed.cs" "$consumer/Host.cs"
expect_failure
grep -q 'Host.cs(3,1): error AU1001' "$work/build.log"
printf 'public static class Checks { public static bool Allowed(string text) => true; }\n' > "$consumer/Checks.cs"
build || { cat "$work/build.log"; exit 1; }
printf 'public static class Checks { public static bool Allowed(string text) => false; }\n' > "$consumer/Checks.cs"
expect_failure
grep -q 'AU1001' "$work/build.log"
# Configuration changes and exclusions apply to closed native sources.
sed 's/^forbidden$/allowed/' "$consumer/Host.cs" > "$consumer/changed.cs"
mv "$consumer/changed.cs" "$consumer/Host.cs"
printf 'public static class Checks { public static bool Allowed(string text) => !text.Contains("forbidden"); }\n' > "$consumer/Checks.cs"
printf 'public sealed class forbidden {}\n' > "$consumer/Other.cs"
printf '{"augmentations":[{"language":"%s","files":["Other.cs"]}]}\n' "$configReference" > "$consumer/nitrogen.json"
expect_failure
grep -q 'Other.cs(1,1): error AU1001' "$work/build.log"
printf '{"augmentations":[{"language":"%s","files":["Other.cs"],"exclude":["Other.cs"]}]}\n' "$configReference" > "$consumer/nitrogen.json"
build || { cat "$work/build.log"; exit 1; }
# A dependency present on disk but excluded from registered project inputs cannot be loaded.
cp "$consumer/Rules.syntax" "$work/Rules.original"
sed 's/start Rules.Document;/start Rules.Document; grammars "Hidden.part";/' "$work/Rules.original" > "$consumer/Rules.syntax"
printf 'syntax module Hidden {}\n' > "$consumer/Hidden.part"
sed 's#</Project>#<ItemGroup><None Remove="Hidden.part"/></ItemGroup></Project>#' "$consumer/Native.csproj" > "$work/Native.modified"
mv "$work/Native.modified" "$consumer/Native.csproj"
expect_failure
grep -q 'missing registered grammar input' "$work/build.log"
cp "$work/Rules.original" "$consumer/Rules.syntax"
build || { cat "$work/build.log"; exit 1; }
# Primary programs share exported declarations in the packaged checker.
cat > "$consumer/Names.syntax" <<'NGR'
#!nitrogen grammar
language Names { start Names.Program; }
syntax module Names {
 symbols { value }
 token Id = ['a'..'z']+ except "let" "use";
 syntax Program = Items:Statement*;
 syntax Statement = Declare / Reference;
 syntax Declare = "let" Name:Id ";" declares value Name in file export;
 syntax Reference = "use" Name:Id ";" references value Name;
}
NGR
printf '#!nitrogen language Names\nlet item;\n' > "$consumer/defs.txt"
printf '#!nitrogen language Names\nuse item;\n' > "$consumer/use.txt"
build || { cat "$work/build.log"; exit 1; }
printf '#!nitrogen language Names\nuse missing;\n' > "$consumer/use.txt"
expect_failure
grep -q 'NB0001' "$work/build.log"
rm "$consumer/Names.syntax" "$consumer/defs.txt" "$consumer/use.txt"
printf 'public static class Checks { public static bool Allowed(string text) => false; }\n' > "$consumer/Checks.cs"
rm "$consumer/Use.cs"
build -p:NitrogenEnabled=false || { cat "$work/build.log"; exit 1; }
# No signature or glob rules means the checker is not launched even when the package is referenced.
rm "$consumer/Rules.syntax" "$consumer/nitrogen.json"
printf 'public sealed class App {}\n' > "$consumer/Host.cs"
build || { cat "$work/build.log"; exit 1; }
! grep -q 'Nitrogen source check' "$work/build.log"
echo 'Signed build consumer: native compile, source mapping, helper reload, cross-file binding, quoted paths, disabled and unconfigured modes passed.'
