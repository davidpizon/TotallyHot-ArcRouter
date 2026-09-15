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

echo "==> Removing the LaunchDaemon plist"
rm -f "${PLIST_DEST}"

echo "==> Removing installed binaries at ${INSTALL_DIR}"
rm -rf "${INSTALL_DIR}"

cat <<'EOF'
Uninstalled. Left in place, deliberately (see this script's header comment):
  - "/Library/Application Support/TotallyHotArcRouter" (operational data: usage ledger, spend history,
    trained models, secrets, and logs)
  - the '_arcrouter' service account
  - the router's local CA in the System keychain

To remove those too:
  sudo dscl . -delete /Users/_arcrouter
  sudo rm -rf "/Library/Application Support/TotallyHotArcRouter"
  security delete-certificate -c "TotallyHot Arc Router Local CA" /Library/Keychains/System.keychain
EOF
