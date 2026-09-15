#!/usr/bin/env bash
# Installs TotallyHot Arc Router as a LaunchDaemon on macOS (web GUI migration plan Phase P10).
#
# Run from inside an extracted release tarball (totallyhotarcrouter-osx-arm64.tar.gz), as root:
#
#   tar xzf totallyhotarcrouter-osx-arm64.tar.gz
#   cd totallyhotarcrouter-osx-arm64
#   sudo ./packaging/macos/install.sh
#
# Idempotent: re-running after a version upgrade (binaries already at /usr/local/opt/totallyhot-arcrouter)
# unloads the daemon, replaces the files, and reloads it - it does not re-create the '_arcrouter'
# account or regenerate the local CA if either already exists (GetOrCreateCa/GetOrCreate are themselves
# idempotent).

set -euo pipefail

if [[ "$(id -u)" -ne 0 ]]; then
    echo "install.sh must be run as root (sudo ./packaging/macos/install.sh)." >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RELEASE_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
INSTALL_DIR="/usr/local/opt/totallyhot-arcrouter"
# Matches AppDataPaths.ResolveMachineSharedDirectory's macOS default exactly - unlike Linux's
# STATE_DIRECTORY, macOS has no systemd-style env-var-provided override, so this literal path is the
# only directory the router will ever resolve on this platform.
STATE_DIR="/Library/Application Support/TotallyHotArcRouter"
LOGS_DIR="${STATE_DIR}/logs"
LABEL="com.totallyhot.arcrouter"
PLIST_DEST="/Library/LaunchDaemons/${LABEL}.plist"
SERVICE_USER="_arcrouter"

if [[ ! -f "${RELEASE_ROOT}/TotallyHotArcRouter" ]]; then
    echo "Expected a published TotallyHotArcRouter executable at '${RELEASE_ROOT}/TotallyHotArcRouter'." >&2
    echo "Run this script from inside an extracted release tarball, not a source checkout." >&2
    exit 1
fi

echo "==> Removing the quarantine attribute macOS Gatekeeper stamps on a downloaded, unsigned build"
# TODO(signing): once a Developer ID certificate + notarization exist (mirroring release.yml's own
# TODO(signing) for the MSI), this step - and the Gatekeeper prompt it works around - goes away.
xattr -dr com.apple.quarantine "${RELEASE_ROOT}" 2>/dev/null || true

echo "==> Creating the dedicated '${SERVICE_USER}' service account (if missing)"
if ! id -u "${SERVICE_USER}" >/dev/null 2>&1; then
    # dscl has no "useradd" - account creation is several discrete record writes. UID/GID picked from
    # macOS's own reserved system-account range (< 500), below the lowest free id already in use there.
    NEW_UID=$(dscl . -list /Users UniqueID | awk '{print $2}' | sort -n | awk '$1<500 {id=$1} END {print (id<200?200:id)+1}')
    dscl . -create "/Users/${SERVICE_USER}"
    dscl . -create "/Users/${SERVICE_USER}" UserShell /usr/bin/false
    dscl . -create "/Users/${SERVICE_USER}" UniqueID "${NEW_UID}"
    dscl . -create "/Users/${SERVICE_USER}" PrimaryGroupID 20
    dscl . -create "/Users/${SERVICE_USER}" IsHidden 1
fi

echo "==> Unloading any existing LaunchDaemon before the file swap"
launchctl bootout system "${PLIST_DEST}" 2>/dev/null || true

echo "==> Installing binaries to ${INSTALL_DIR}"
mkdir -p "${INSTALL_DIR}"
cp -a "${RELEASE_ROOT}/." "${INSTALL_DIR}/"
chown -R root:wheel "${INSTALL_DIR}"
chmod 755 "${INSTALL_DIR}/TotallyHotArcRouter"

echo "==> Preparing state and log directories"
mkdir -p "${LOGS_DIR}"
chown -R "${SERVICE_USER}:staff" "${STATE_DIR}"

echo "==> Trusting the router's local CA in the System keychain"
# Run as root (not the service account): MacCertificateTrustStore.Install needs an administrator to
# modify /Library/Keychains/System.keychain (see its own remarks). No STATE_DIRECTORY override needed
# here, unlike Linux's install.sh - AppDataPaths resolves the same fixed path regardless of caller.
"${INSTALL_DIR}/TotallyHotArcRouter" --install-certificate
chown -R "${SERVICE_USER}:staff" "${STATE_DIR}"

echo "==> Installing the LaunchDaemon"
cp "${SCRIPT_DIR}/com.totallyhot.arcrouter.plist" "${PLIST_DEST}"
chown root:wheel "${PLIST_DEST}"
chmod 644 "${PLIST_DEST}"

echo "==> Loading ${LABEL}"
launchctl bootstrap system "${PLIST_DEST}"

echo "Installed. Check status with: sudo launchctl print system/${LABEL}"
echo "Print the MCP/gRPC management token with:"
echo "  sudo -u ${SERVICE_USER} ${INSTALL_DIR}/TotallyHotArcRouter --print-management-token"
