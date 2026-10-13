#!/usr/bin/env python3
"""Verify MSBuild registers config-selected grammars without discovering disk-only files."""
import json
import pathlib
import subprocess
import tempfile
from xml.sax.saxutils import escape

repo = pathlib.Path(__file__).resolve().parents[1]
subprocess.run(["dotnet", "build", str(repo / "Nitrogen.Build/Nitrogen.Build.csproj"), "-v:q"], check=True)
assembly = repo / "Nitrogen.Build/bin/Debug/net10.0/Nitrogen.Build.dll"
with tempfile.TemporaryDirectory(prefix="nitrogen-composition-inputs-") as folder:
    root = pathlib.Path(folder)
    (root / "a.part").write_text('syntax module A { syntax Program = "a"; }')
    (root / "unregistered.part").write_text('syntax module Other { syntax Unused = "x"; }')
    (root / "nitrogen.json").write_text(json.dumps({"languages": [{
        "name": "Demo", "start": "A.Program", "grammars": ["*.part"]
    }]}))
    project = root / "check.proj"
    project.write_text(f'''<Project>
  <UsingTask TaskName="Nitrogen.Build.WriteAnalysisManifest" AssemblyFile="{escape(str(assembly))}" />
  <ItemGroup>
    <Input Include="{escape(str(root / 'a.part'))}" />
    <Input Include="{escape(str(root / 'nitrogen.json'))}" />
  </ItemGroup>
  <Target Name="Check">
    <Nitrogen.Build.WriteAnalysisManifest Root="{escape(str(root))}" ManifestPath="{escape(str(root / 'manifest.json'))}" Inputs="@(Input)">
      <Output TaskParameter="GeneratorInputs" ItemName="Selected" />
    </Nitrogen.Build.WriteAnalysisManifest>
    <WriteLinesToFile File="{escape(str(root / 'selected.txt'))}" Lines="@(Selected)" Overwrite="true" />
  </Target>
</Project>''')
    subprocess.run(["dotnet", "msbuild", str(project), "-t:Check", "-v:q", "-nr:false"], check=True)
    selected = {pathlib.Path(p).resolve() for p in (root / "selected.txt").read_text().splitlines() if p}
    assert selected == {(root / "a.part").resolve(), (root / "nitrogen.json").resolve()}, selected
print("Composition MSBuild input registration passed")
