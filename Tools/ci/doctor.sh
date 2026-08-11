#!/usr/bin/env bash
# Reports what this machine can actually do with the Strada.Core toolchain, and what is
# missing. Run it first when something in Tools/ci misbehaves, or after installing the
# Unity CLI to confirm the repository picked it up.
set -uo pipefail

. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/unity-env.sh"

ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$1"; }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$1"; }

echo "Strada.Core toolchain"
echo

echo "Unity CLI"
if [ -n "$UNITY_CLI" ]; then
    ok "$UNITY_CLI ($("$UNITY_CLI" --version 2>/dev/null | head -1))"
    # Service-account credentials are the documented way to authenticate a CI runner;
    # interactive `unity auth login` is not usable from a pipeline.
    if [ -n "${UNITY_SERVICE_ACCOUNT_ID:-}" ] && [ -n "${UNITY_SERVICE_ACCOUNT_SECRET:-}" ]; then
        ok "service account credentials present in the environment"
    else
        warn "no UNITY_SERVICE_ACCOUNT_ID / UNITY_SERVICE_ACCOUNT_SECRET (needed for unattended CI)"
    fi
    if "$UNITY_CLI" test --help >/dev/null 2>&1; then
        ok "'unity test' is available — see the note in run-tests.sh about switching to it"
    else
        warn "'unity test' not available in this CLI version"
    fi
else
    warn "not installed. Provisioning and CI auth still work without it, via the Hub and an"
    warn "explicit UNITY path, but 'unity install' and service-account auth need it:"
    warn "    brew install --cask unity-cli"
fi
echo

echo "Editor"
if [ -n "$UNITY_EDITOR" ] && [ -x "$UNITY_EDITOR" ]; then
    ok "$UNITY_VERSION"
    echo "    $UNITY_EDITOR"
    [ "$UNITY_VERSION" = "$STRADA_UNITY_VERSION" ] \
        || warn "repository targets $STRADA_UNITY_VERSION"
else
    bad "no editor found (set UNITY, or run: unity install $STRADA_UNITY_VERSION)"
fi
echo

echo "Package"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
[ -f "$REPO/package.json" ] && ok "package.json" || bad "package.json missing"
if [ -d "$REPO/Assets" ] || [ -d "$REPO/ProjectSettings" ]; then
    warn "Assets/ or ProjectSettings/ present — this repo is a package, not a project"
else
    ok "no Assets/ or ProjectSettings/ (expected: assemble-bench-project.sh builds the host)"
fi
[ -f "$REPO/Runtime/Analyzers/Strada.SourceGeneration.dll" ] \
    && ok "source generator analyzer is present" \
    || bad "Runtime/Analyzers/Strada.SourceGeneration.dll missing (run build-generators.sh)"
echo

echo "Next"
echo "  ./Tools/ci/assemble-bench-project.sh \"\$PWD\" /tmp/StradaBench"
echo "  ./Tools/ci/compile.sh   /tmp/StradaBench"
echo "  ./Tools/ci/run-tests.sh /tmp/StradaBench playmode"
