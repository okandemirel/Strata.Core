#!/usr/bin/env bash
# Runs the package test suite through the synthesised host project.
#
#   usage: run-tests.sh <project> [editmode|playmode] [extra editor args...]
#
# Extra arguments are forwarded to the editor, e.g.
#   run-tests.sh /tmp/StradaBench playmode -testCategory "NoAlloc"
#
# Uses `unity test` when the Unity CLI is installed and falls back to driving the editor
# in batchmode otherwise. Both paths write the same NUnit XML, which the summary below
# parses, so the output is identical either way.
set -uo pipefail
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/unity-env.sh"

PROJ=${1:?project path required}
MODE=${2:-editmode}
OUT="$PROJ/../test-$MODE.xml"
LOG="$PROJ/../test-$MODE.log"
shift 2 || true

# Kills a hung editor rather than letting a CI job sit until the runner's own limit.
: "${STRADA_TEST_TIMEOUT:=1800}"

if [ -n "$UNITY_CLI" ]; then
    # The CLI wants EditMode/PlayMode; this script's own argument is lowercase.
    case "$MODE" in
        editmode) CLI_MODE=EditMode ;;
        playmode) CLI_MODE=PlayMode ;;
        *)        echo "unknown mode '$MODE' (expected editmode or playmode)" >&2; exit 2 ;;
    esac

    # --allow-install provisions the editor named in the project's ProjectVersion.txt, which
    # is what makes a clean CI runner work with no separate install step. Arguments after
    # `--` are handed to the editor unchanged, which is how -testCategory still works.
    UNITY_NO_BANNER=1 UNITY_NON_INTERACTIVE=1 \
    "$UNITY_CLI" test "$PROJ" \
        --mode "$CLI_MODE" \
        --output "$OUT" \
        --timeout "$STRADA_TEST_TIMEOUT" \
        --allow-install \
        -- -nographics -silent-crashes -accept-apiupdate "$@" >"$LOG" 2>&1
    echo "unity test exit: $?"
else
    strada_require_editor
    "$UNITY_EDITOR" -batchmode -nographics -silent-crashes -accept-apiupdate \
      -projectPath "$PROJ" -runTests -testPlatform "$MODE" \
      -testResults "$OUT" -logFile "$LOG" "$@" >/dev/null 2>&1
    echo "unity exit: $?"
fi

python3 - "$OUT" <<'PY'
import sys,xml.etree.ElementTree as ET
try: r=ET.parse(sys.argv[1]).getroot()
except Exception as e: print("could not parse results:",e); sys.exit(0)
print("total=%s passed=%s failed=%s skipped=%s inconclusive=%s duration=%ss" % (
  r.get('total'),r.get('passed'),r.get('failed'),r.get('skipped'),r.get('inconclusive'),r.get('duration')))
for tc in r.iter('test-case'):
    if tc.get('result') not in ('Passed','Skipped'):
        msg=tc.find('.//message')
        print("FAIL %s :: %s" % (tc.get('fullname'), (msg.text or '').strip().splitlines()[0] if msg is not None and msg.text else ''))
PY
