#!/usr/bin/env bash
# Removes the LaunchDaemon and installed binaries (web GUI migration plan Phase P10). Does NOT remove
# "/Library/Application Support/TotallyHotArcRouter" - matching the MSI's own deliberate choice
# (docs/router/packaging-and-distribution.md §3.1) to preserve operator data (the usage ledger, provider
# spend history, trained voter models, and secrets) across an uninstall. Does NOT remove the local CA
# from the System keychain or delete the '_arcrouter' account either, for the same "don't silently
# destroy state an operator might still want" reasoning - both are left as an explicit manual follow-up,
# printed at the end.

set -euo pipefail

if [[ "$(id -u)" -ne 0 ]]; then
    echo "uninstall.sh must be run as root (sudo ./packaging/macos/uninstall.sh)." >&2
    exit 1
fi

LABEL="com.totallyhot.arcrouter"
PLIST_DEST="/Library/LaunchDaemons/${LABEL}.plist"
INSTALL_DIR="/usr/local/opt/totallyhot-arcrouter"

echo "==> Unloading ${LABEL}"
launchctl bootout system "${PLIST_DEST}" 2>/dev/null || true

echo "==> Removing conversation text (transcripts and body-excerpt logs; spend data is kept)"
# #184 / ADR-0024: run as the service account, never root - root would leave a root-owned secrets.dat the
# service cannot read after a reinstall.
SHRED_RESULT="/Library/Application Support/TotallyHotArcRouter/shred-conversations.result"
if ! [[ -x "${INSTALL_DIR}/TotallyHotArcRouter" ]] || ! id _arcrouter >/dev/null 2>&1; then
    echo "Skipped: the router binary or the '_arcrouter' account is already gone."
elif launchctl print "system/${LABEL}" >/dev/null 2>&1; then
    # bootout failures are ignored above; a still-loaded daemon could write a new transcript or body log
    # after the shred, so removal would be reported complete while text remains.
    echo "WARNING: ${LABEL} is still loaded, so conversation text was NOT removed." >&2
    echo "         Unload it, then run: sudo -H -u _arcrouter ${INSTALL_DIR}/TotallyHotArcRouter --shred-conversations" >&2
else
    # The shred is a separate process: it does not inherit the daemon's environment, so forward the one
    # override that changes which database it targets (read from the plist while it still exists).
    shred_env=()
    override="$(/usr/libexec/PlistBuddy -c "Print :EnvironmentVariables:Storage__TranscriptDatabasePath" "${PLIST_DEST}" 2>/dev/null || true)"
    [[ -n "${override}" ]] && shred_env+=("Storage__TranscriptDatabasePath=${override}")
    sudo -H -u _arcrouter env ${shred_env[@]+"${shred_env[@]}"} \
        "${INSTALL_DIR}/TotallyHotArcRouter" --shred-conversations \
        || echo "WARNING: some conversation text could not be removed; see \"${SHRED_RESULT}\"." >&2
fi

echo "==> Removing the LaunchDaemon plist"
rm -f "${PLIST_DEST}"

echo "==> Removing installed binaries at ${INSTALL_DIR}"
rm -rf "${INSTALL_DIR}"

cat <<'EOF'
Uninstalled. Removed: conversation text (transcript rows, body-excerpt logs).
Left in place, deliberately (see this script's header comment):
  - "/Library/Application Support/TotallyHotArcRouter" (operational data: usage ledger, spend history,
    trained models, secrets, and diagnostic logs)
  - the '_arcrouter' service account
  - the router's local CA in the System keychain

To remove those too:
  sudo dscl . -delete /Users/_arcrouter
  sudo rm -rf "/Library/Application Support/TotallyHotArcRouter"
  security delete-certificate -c "TotallyHot Arc Router Local CA" /Library/Keychains/System.keychain
EOF
