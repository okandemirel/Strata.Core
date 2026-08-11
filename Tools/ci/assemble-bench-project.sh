#!/usr/bin/env bash
# Synthesises a Unity host project that references com.strada.core as a local package,
# so that Unity's Test Runner can discover and execute the package's tests.
#
# The package repo deliberately contains no Assets/ or ProjectSettings/, so no Unity
# binary can open it directly. This script builds the missing shell around it.
#
#   usage: assemble-bench-project.sh <package-root> <output-project-dir>
set -euo pipefail

PKG_ROOT="${1:?package root required}"
PROJ="${2:?output project dir required}"

PKG_ROOT="$(cd "$PKG_ROOT" && pwd)"

rm -rf "$PROJ"
mkdir -p "$PROJ/Assets" "$PROJ/Packages" "$PROJ/ProjectSettings"

# --- Packages/manifest.json -------------------------------------------------
# `testables` is mandatory: without it the Test Runner does not discover tests
# inside a file:-referenced package, and the whole run silently reports 0 tests.
cat > "$PROJ/Packages/manifest.json" <<JSON
{
  "dependencies": {
    "com.strada.core": "file:$PKG_ROOT",
    "com.unity.entities": "1.3.8",
    "com.unity.burst": "1.8.24",
    "com.unity.collections": "2.5.1",
    "com.unity.mathematics": "1.3.2",
    "com.unity.test-framework": "1.4.5",
    "com.unity.test-framework.performance": "3.1.0",
    "com.unity.modules.jsonserialize": "1.0.0",
    "com.unity.modules.imgui": "1.0.0",
    "com.unity.modules.uielements": "1.0.0",
    "com.unity.modules.ui": "1.0.0",
    "com.unity.modules.physics": "1.0.0",
    "com.unity.modules.animation": "1.0.0",
    "com.unity.modules.unitywebrequest": "1.0.0"
  },
  "testables": [
    "com.strada.core"
  ]
}
JSON

# --- ProjectSettings --------------------------------------------------------
cat > "$PROJ/ProjectSettings/ProjectVersion.txt" <<TXT
m_EditorVersion: 6000.5.7f1
TXT

cat > "$PROJ/ProjectSettings/ProjectSettings.asset" <<'YAML'
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!129 &1
PlayerSettings:
  m_ObjectHideFlags: 0
  serializedVersion: 26
  productName: StradaBench
  companyName: Strada
  apiCompatibilityLevel: 6
  scriptingBackend: {}
  gcIncremental: 1
  activeInputHandler: 0
YAML

# --- CI build helper (IL2CPP config cannot be set from the CLI) -------------
mkdir -p "$PROJ/Assets/Editor"
cat > "$PROJ/Assets/Editor/CiBuild.cs" <<'CSHARP'
using UnityEditor;
using UnityEditor.Build;

public static class CiBuild
{
    public static void ConfigureIl2Cpp()
    {
        var nbt = NamedBuildTarget.Standalone;
        PlayerSettings.SetScriptingBackend(nbt, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetIl2CppCompilerConfiguration(nbt, Il2CppCompilerConfiguration.Master);
        PlayerSettings.SetIl2CppCodeGeneration(nbt, UnityEditor.Build.Il2CppCodeGeneration.OptimizeSpeed);
        PlayerSettings.SetManagedStrippingLevel(nbt, ManagedStrippingLevel.Low);
        PlayerSettings.SetApiCompatibilityLevel(nbt, ApiCompatibilityLevel.NET_Standard);
        EditorUserBuildSettings.development = false;
    }

    // Compile-only gate. Compilation already happened during project load, so if we
    // reach this method at all every assembly built; Unity exits non-zero on its own
    // ("Scripts have compiler errors.") when it did not.
    public static void CompileOnly()
    {
        EditorApplication.Exit(0);
    }
}
CSHARP

echo "Host project assembled at: $PROJ"
echo "  package  : $PKG_ROOT"
echo "  testables: com.strada.core"
