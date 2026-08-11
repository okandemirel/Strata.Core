#!/usr/bin/env bash
# Runs the package test suite through the synthesised host project.
#   usage: run-tests.sh <project> [editmode|playmode] [extra unity args...]
set -uo pipefail
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/unity-env.sh"
strada_require_editor
PROJ=${1:?project path required}
MODE=${2:-editmode}
OUT="$PROJ/../test-$MODE.xml"
LOG="$PROJ/../test-$MODE.log"
shift 2 || true
# Deliberately the editor executable rather than `unity test`. The CLI has a test
# subcommand, but its flags are not in the published reference (the docs name
# `unity --help` as the authority) and it is still marked experimental — whereas this
# invocation is the one this repository has actually run. To switch: confirm the flags
# with `unity test --help`, then replace this block with the equivalent CLI call and
# keep -testResults so the parser below still works.
"$UNITY_EDITOR" -batchmode -nographics -silent-crashes -accept-apiupdate \
  -projectPath "$PROJ" -runTests -testPlatform "$MODE" \
  -testResults "$OUT" -logFile "$LOG" "$@" >/dev/null 2>&1
echo "unity exit: $?"
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
