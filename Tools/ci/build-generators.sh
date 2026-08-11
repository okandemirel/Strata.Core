#!/usr/bin/env bash
# Builds the Roslyn source generators and installs the result into Editor/Analyzers/.
#
# Uses the .NET SDK and the Roslyn assemblies bundled with the Unity editor, so no
# network access or separate SDK install is required, and the analyzer is compiled
# against the same compiler version that loads it.
set -euo pipefail

UNITY_ROOT=${UNITY_ROOT:-/Applications/Unity/Hub/Editor/6000.5.7f1/Unity.app/Contents}
DOTNET="$UNITY_ROOT/Resources/Scripting/DotNetSdk/dotnet"
ROSLYN="$UNITY_ROOT/Resources/BuildPipeline/Unity.Analyzers.Common"

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJ="$REPO/SourceGeneration~/Strada.SourceGeneration.csproj"
OUT="$REPO/SourceGeneration~/bin"

[ -x "$DOTNET" ] || { echo "dotnet not found at $DOTNET (set UNITY_ROOT)"; exit 1; }
[ -f "$ROSLYN/Microsoft.CodeAnalysis.CSharp.dll" ] || { echo "Roslyn not found at $ROSLYN"; exit 1; }

"$DOTNET" build "$PROJ" -c Release -o "$OUT" \
  -p:RoslynPath="$ROSLYN" \
  --nologo

cp "$OUT/Strada.SourceGeneration.dll" "$REPO/Runtime/Analyzers/Strada.SourceGeneration.dll"
echo "installed -> Runtime/Analyzers/Strada.SourceGeneration.dll"
