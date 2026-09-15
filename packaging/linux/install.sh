#!/usr/bin/env bash
# Installs TotallyHot Arc Router as a systemd service on Linux (web GUI migration plan Phase P10).
#
# Run from inside an extracted release tarball (totallyhotarcrouter-linux-x64.tar.gz or
# totallyhotarcrouter-linux-arm64.tar.gz), as root:
#
#   tar xzf totallyhotarcrouter-linux-x64.tar.gz
#   cd totallyhotarcrouter-linux-x64
#   sudo ./packaging/linux/install.sh
#
# Idempotent: re-running after a version upgrade (binaries already at /opt/totallyhot-arcrouter) stops
# the service, replaces the files, and restarts it - it does not re-create the 'arcrouter' account or
# regenerate the local CA if either already exists (GetOrCreateCa/GetOrCreate are themselves idempotent).

set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
    echo "install.sh must be run as root (sudo ./packaging/linux/install.sh)." >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RELEASE_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
INSTALL_DIR="/opt/totallyhot-arcrouter"
# Matches AppDataPaths' own Linux defaults exactly (ResolveMachineSharedDirectory's non-systemd
# fallback and StateDirectory=/LogsDirectory=totallyhot-arcrouter in the unit file below), so the
# router resolves the same directories whether it is invoked here (as root, for CA setup) or later by
# systemd (as the 'arcrouter' account).
STATE_DIR="/var/lib/totallyhot-arcrouter"
LOGS_DIR="/var/log/totallyhot-arcrouter"
SERVICE_NAME="totallyhot-arcrouter"
SERVICE_USER="arcrouter"

if [[ ! -f "${RELEASE_ROOT}/TotallyHotArcRouter" ]]; then
    echo "Expected a published TotallyHotArcRouter executable at '${RELEASE_ROOT}/TotallyHotArcRouter'." >&2
    echo "Run this script from inside an extracted release tarball, not a source checkout." >&2
    exit 1
fi

echo "==> Creating the dedicated '${SERVICE_USER}' system account (if missing)"
if ! id -u "${SERVICE_USER}" >/dev/null 2>&1; then
    useradd --system --no-create-home --shell /usr/sbin/nologin "${SERVICE_USER}"
fi

echo "==> Stopping any existing service before the file swap"
systemctl stop "${SERVICE_NAME}.service" 2>/dev/null || true

echo "==> Installing binaries to ${INSTALL_DIR}"
mkdir -p "${INSTALL_DIR}"
# Additive, not --delete: an operator may have dropped their own files alongside the published ones.
cp -a "${RELEASE_ROOT}/." "${INSTALL_DIR}/"
chown -R root:root "${INSTALL_DIR}"
chmod 755 "${INSTALL_DIR}/TotallyHotArcRouter"

echo "==> Preparing state and log directories"
mkdir -p "${STATE_DIR}" "${LOGS_DIR}"

echo "==> Installing the systemd unit"
cp "${SCRIPT_DIR}/totallyhot-arcrouter.service" "/etc/systemd/system/${SERVICE_NAME}.service"
systemctl daemon-reload

echo "==> Generating (or reusing) the router's local CA and trusting it system-wide"
# Run as root, not the service account: LinuxCertificateTrustStore.Install needs root to write under
# /usr/local/share/ca-certificates and run update-ca-certificates (see its own remarks). STATE_DIRECTORY
# is set explicitly here so the CA's private key lands in the exact directory the systemd unit's own
# StateDirectory=totallyhot-arcrouter grants the service at runtime; ownership is handed to the service
# account below once generation is done.
STATE_DIRECTORY="${STATE_DIR}" "${INSTALL_DIR}/TotallyHotArcRouter" --install-certificate

chown -R "${SERVICE_USER}:${SERVICE_USER}" "${STATE_DIR}" "${LOGS_DIR}"

echo "==> Enabling and starting ${SERVICE_NAME}.service"
systemctl enable --now "${SERVICE_NAME}.service"

echo "Installed. Check status with: systemctl status ${SERVICE_NAME}.service"
echo "Print the MCP/gRPC management token with:"
echo "  sudo -u ${SERVICE_USER} STATE_DIRECTORY=${STATE_DIR} ${INSTALL_DIR}/TotallyHotArcRouter --print-management-token"
