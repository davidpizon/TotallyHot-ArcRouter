#!/usr/bin/env bash
# Removes the systemd service and installed binaries (web GUI migration plan Phase P10). Does NOT
# remove /var/lib/totallyhot-arcrouter or /var/log/totallyhot-arcrouter - matching the MSI's own
# deliberate choice (docs/router/packaging-and-distribution.md §3.1) to preserve operator data (the
# usage ledger, provider spend history, trained voter models, and secrets) across an uninstall. Does NOT
# remove the local CA from the system trust store or delete the 'arcrouter' system account either, for
# the same "don't silently destroy state an operator might still want" reasoning - both are left as an
# explicit manual follow-up, printed at the end.

set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
    echo "uninstall.sh must be run as root (sudo ./packaging/linux/uninstall.sh)." >&2
    exit 1
fi

SERVICE_NAME="totallyhot-arcrouter"
INSTALL_DIR="/opt/totallyhot-arcrouter"

echo "==> Stopping and disabling ${SERVICE_NAME}.service"
systemctl disable --now "${SERVICE_NAME}.service" 2>/dev/null || true

echo "==> Removing conversation text (transcripts and body-excerpt logs; spend data is kept)"
# #184 / ADR-0024: run as the service account, never root - root would leave a root-owned secrets.dat the
# service cannot read after a reinstall. STATE_DIRECTORY and LOGS_DIRECTORY are what systemd exports to
# the unit; without them the router would look for the logs under the state directory.
if [[ -x "${INSTALL_DIR}/TotallyHotArcRouter" ]] && id arcrouter >/dev/null 2>&1; then
    runuser -u arcrouter -- env \
        STATE_DIRECTORY=/var/lib/totallyhot-arcrouter \
        LOGS_DIRECTORY=/var/log/totallyhot-arcrouter \
        "${INSTALL_DIR}/TotallyHotArcRouter" --shred-conversations \
        || echo "WARNING: some conversation text could not be removed; see /var/lib/totallyhot-arcrouter/shred-conversations.result." >&2
else
    echo "Skipped: the router binary or the 'arcrouter' account is already gone."
fi

echo "==> Removing the systemd unit"
rm -f "/etc/systemd/system/${SERVICE_NAME}.service"
systemctl daemon-reload

echo "==> Removing installed binaries at ${INSTALL_DIR}"
rm -rf "${INSTALL_DIR}"

cat <<'EOF'
Uninstalled. Removed: conversation text (transcript rows, body-excerpt logs).
Left in place, deliberately (see this script's header comment):
  - /var/lib/totallyhot-arcrouter (operational data: usage ledger, spend history, trained models, secrets)
  - /var/log/totallyhot-arcrouter (diagnostic log files)
  - the 'arcrouter' system account
  - the router's local CA in the system trust store

To remove those too:
  sudo userdel arcrouter
  sudo rm -rf /var/lib/totallyhot-arcrouter /var/log/totallyhot-arcrouter
  sudo rm -f /usr/local/share/ca-certificates/totallyhot-arcrouter-ca.crt && sudo update-ca-certificates
EOF
