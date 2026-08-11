#!/usr/bin/env bash
# Recompiles the package through the synthesised host project and reports only real errors.
set -uo pipefail
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/unity-env.sh"
strada_require_editor
PROJ=${1:?project path required}
LOG=${2:-$PROJ/../unity-compile.log}
"$UNITY_EDITOR" -batchmode -quit -nographics -silent-crashes \
  -projectPath "$PROJ" -executeMethod CiBuild.CompileOnly -logFile "$LOG" >/dev/null 2>&1
echo "unity exit: $?"
ERRS=$(grep -oE '[^ ]+\.cs\([0-9]+,[0-9]+\): error [A-Z]+[0-9]+: .*' "$LOG" | sort -u)
if [ -n "$ERRS" ]; then echo "$ERRS"; else echo "no compiler errors"; fi
