#!/usr/bin/env bash
# Write one `dotnet list package --vulnerable` JSON file per cross-platform project.
# TotallyHotArcRouter.Tray targets net10.0-windows and is omitted, matching Linux CI
# and src/TotallyHotArcRouter.Qodana.slnx. A non-zero exit from `dotnet list` means
# vulnerable packages were found; a real failure leaves no JSON.

set -euo pipefail

out_dir="${1:?output directory required}"
mkdir -p "$out_dir"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

mapfile -t projects < <(
  find src -name '*.csproj' \
    -not -path '*/obj/*' \
    -not -path '*/bin/*' \
    -print | sort
)

if [ "${#projects[@]}" -eq 0 ]; then
  echo "::error::no projects found under src/"
  exit 1
fi

found=0
for proj in "${projects[@]}"; do
  case "$proj" in
    src/TotallyHotArcRouter.Tray/TotallyHotArcRouter.Tray.csproj)
      echo "Skipping $proj (net10.0-windows)"
      continue
      ;;
  esac
  found=1
  dest="$out_dir/$(basename "$proj" .csproj).json"
  if [ -e "$dest" ]; then
    echo "::error::two projects share the output name $dest"
    exit 1
  fi
  echo "::group::restore and audit $proj"
  dotnet restore "$proj"
  status=0
  dotnet list "$proj" package --vulnerable --include-transitive --format json >"$dest" || status=$?
  if [ ! -s "$dest" ]; then
    echo "::error::dotnet list wrote no JSON for $proj (exit $status)"
    exit 1
  fi
  echo "dotnet list exit $status (non-zero means vulnerable packages were reported)"
  echo "::endgroup::"
done

if [ "$found" -ne 1 ]; then
  echo "::error::no cross-platform projects to audit"
  exit 1
fi
