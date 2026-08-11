#!/usr/bin/env bash
# Resolves the Unity toolchain for every script in Tools/ci. Source it; do not execute it.
#
#   . "$(dirname "${BASH_SOURCE[0]}")/unity-env.sh"
#
# Exports:
#   UNITY_CLI     path to the `unity` CLI, or empty when it is not installed
#   UNITY_EDITOR  path to the editor executable used for batchmode invocations
#   UNITY_VERSION editor version string that UNITY_EDITOR corresponds to
#
# WHY BOTH. Unity shipped a first-party CLI (`unity`, July 2026) that supersedes the Hub
# for provisioning and exposes `unity test` / `unity build` / `unity run`. It is marked
# EXPERIMENTAL, its subcommand flags are not fully published — the docs point at
# `unity --help` as the authority — and it is not installed everywhere yet. So:
#
#   * The CLI is preferred for things whose syntax is documented and stable: locating and
#     installing editors, auth, diagnostics.
#   * The actual compile and test invocations still go through the editor executable in
#     batchmode, which is the path this repository has actually exercised.
#
# Switching the test run over to `unity test` is a one-line change in run-tests.sh once
# someone confirms its flags locally; see the note there.

# Version this repository is developed against. Override with STRADA_UNITY_VERSION.
: "${STRADA_UNITY_VERSION:=6000.5.7f1}"

_strada_uname="$(uname -s)"

# --- the CLI, if present ----------------------------------------------------------------
UNITY_CLI="$(command -v unity 2>/dev/null || true)"

# --- the editor executable ----------------------------------------------------------------
# An explicit UNITY always wins, so a caller can pin an exact editor without touching this.
if [ -n "${UNITY:-}" ]; then
    UNITY_EDITOR="$UNITY"
    UNITY_VERSION="${STRADA_UNITY_VERSION}"
else
    case "$_strada_uname" in
        Darwin)  _strada_hub="/Applications/Unity/Hub/Editor"
                 _strada_exe="Unity.app/Contents/MacOS/Unity" ;;
        Linux)   _strada_hub="$HOME/Unity/Hub/Editor"
                 _strada_exe="Editor/Unity" ;;
        *)       _strada_hub=""
                 _strada_exe="" ;;
    esac

    UNITY_EDITOR=""
    UNITY_VERSION=""

    if [ -n "$_strada_hub" ] && [ -x "$_strada_hub/$STRADA_UNITY_VERSION/$_strada_exe" ]; then
        UNITY_EDITOR="$_strada_hub/$STRADA_UNITY_VERSION/$_strada_exe"
        UNITY_VERSION="$STRADA_UNITY_VERSION"
    elif [ -n "$_strada_hub" ] && [ -d "$_strada_hub" ]; then
        # Fall back to the newest installed editor rather than failing outright. Sort is
        # lexical, which orders Unity's version strings correctly within a major line.
        for _strada_candidate in $(ls -1 "$_strada_hub" 2>/dev/null | sort -r); do
            if [ -x "$_strada_hub/$_strada_candidate/$_strada_exe" ]; then
                UNITY_EDITOR="$_strada_hub/$_strada_candidate/$_strada_exe"
                UNITY_VERSION="$_strada_candidate"
                break
            fi
        done
    fi
fi

export UNITY_CLI UNITY_EDITOR UNITY_VERSION

# --- provisioning helper -------------------------------------------------------------------
# Installs the required editor through the CLI when it is missing. Only reachable when the
# CLI is present; `unity install <version>` is documented and stable.
strada_ensure_editor() {
    if [ -n "$UNITY_EDITOR" ] && [ -x "$UNITY_EDITOR" ]; then
        return 0
    fi

    if [ -z "$UNITY_CLI" ]; then
        echo "No Unity editor found and the Unity CLI is not installed." >&2
        echo "Install the CLI:  brew install --cask unity-cli" >&2
        echo "                  (or see https://docs.unity.com/en-us/unity-cli/use-unity-cli)" >&2
        echo "Then:             unity install $STRADA_UNITY_VERSION" >&2
        echo "Or point UNITY at an editor executable directly." >&2
        return 1
    fi

    echo "Editor $STRADA_UNITY_VERSION not found; installing it with the Unity CLI."
    # --non-interactive so a CI runner never blocks on a prompt.
    "$UNITY_CLI" install "$STRADA_UNITY_VERSION" --non-interactive || return 1

    # Re-resolve after the install by re-sourcing this file.
    . "${BASH_SOURCE[0]}"
    [ -n "$UNITY_EDITOR" ] && [ -x "$UNITY_EDITOR" ]
}

strada_require_editor() {
    strada_ensure_editor || exit 1
    if [ "$UNITY_VERSION" != "$STRADA_UNITY_VERSION" ]; then
        echo "note: using editor $UNITY_VERSION (wanted $STRADA_UNITY_VERSION)" >&2
    fi
}
