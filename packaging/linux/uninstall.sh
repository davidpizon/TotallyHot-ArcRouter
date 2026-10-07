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
SHRED_RESULT="/var/lib/totallyhot-arcrouter/shred-conversations.result"
if ! [[ -x "${INSTALL_DIR}/TotallyHotArcRouter" ]] || ! id arcrouter >/dev/null 2>&1; then
    echo "Skipped: the router binary or the 'arcrouter' account is already gone."
elif systemctl is-active --quiet "${SERVICE_NAME}.service" 2>/dev/null; then
    # The stop above ignores failures; a live service could write a new transcript or body log after the
    # shred, so removal would be reported complete while text remains.
    echo "WARNING: ${SERVICE_NAME}.service is still running, so conversation text was NOT removed." >&2
    echo "         Stop it, then run: runuser -u arcrouter -- ${INSTALL_DIR}/TotallyHotArcRouter --shred-conversations" >&2
else
    # The shred is a separate process: it does not inherit the unit's environment, so forward the one
    # override that changes which database it targets (read from the unit while it still exists).
    shred_env=(STATE_DIRECTORY=/var/lib/totallyhot-arcrouter LOGS_DIRECTORY=/var/log/totallyhot-arcrouter)
    for kv in $(systemctl show -p Environment --value "${SERVICE_NAME}.service" 2>/dev/null); do
        [[ "${kv}" == Storage__TranscriptDatabasePath=* ]] && shred_env+=("${kv}")
    done
    runuser -u arcrouter -- env "${shred_env[@]}" \
        "${INSTALL_DIR}/TotallyHotArcRouter" --shred-conversations \
        || echo "WARNING: some conversation text could not be removed; see ${SHRED_RESULT}." >&2
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
