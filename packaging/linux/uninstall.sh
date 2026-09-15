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

echo "==> Removing the systemd unit"
rm -f "/etc/systemd/system/${SERVICE_NAME}.service"
systemctl daemon-reload

echo "==> Removing installed binaries at ${INSTALL_DIR}"
rm -rf "${INSTALL_DIR}"

cat <<'EOF'
Uninstalled. Left in place, deliberately (see this script's header comment):
  - /var/lib/totallyhot-arcrouter (operational data: usage ledger, spend history, trained models, secrets)
  - /var/log/totallyhot-arcrouter (log files)
  - the 'arcrouter' system account
  - the router's local CA in the system trust store

To remove those too:
  sudo userdel arcrouter
  sudo rm -rf /var/lib/totallyhot-arcrouter /var/log/totallyhot-arcrouter
  sudo rm -f /usr/local/share/ca-certificates/totallyhot-arcrouter-ca.crt && sudo update-ca-certificates
EOF
