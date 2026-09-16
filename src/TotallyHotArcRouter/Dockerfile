# TotallyHot Arc Router container image, linux-x64 (web GUI migration plan Phase P10).
#
# Build (from the repo root - src/TotallyHotArcRouter.Gui.Web is a build-time ProjectReference, so the
# whole src/ tree is needed in the build context, not just this project's own directory):
#   docker build -t totallyhot-arcrouter -f src/TotallyHotArcRouter/Dockerfile .
#
# Run - only the loopback-bound ports are published to the host by default; widen WebInterface/Proxy/Mcp
# BindAddress to 0.0.0.0 (already this image's default - see the ENV lines below) only if you also trust
# whatever network the host port is reachable from:
#   docker run -d --name arcrouter \
#     -p 127.0.0.1:47101:47101 -p 127.0.0.1:47104:47104 \
#     -v arcrouter-data:/data \
#     totallyhot-arcrouter
#
# First-time trust setup (from the host, once the container is running):
#   docker exec arcrouter dotnet TotallyHotArcRouter.dll --export-ca
#   docker cp arcrouter:/data/router-ca.crt .   # then trust router-ca.crt locally - see
#                                                # docs/router/client-tls-setup.md
#   docker exec arcrouter dotnet TotallyHotArcRouter.dll --print-management-token
#
# The opt-in plain-HTTP fallback (Proxy:PlainHttp) is deliberately never published here: it is always
# loopback-only inside the container's own network namespace (ProxyListenerOptions' remarks), so
# publishing its port would just map to a listener nothing outside the container can ever reach anyway.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# No separate `dotnet restore` layer: a standalone "dotnet restore -r linux-x64" on
# TotallyHotArcRouter.csproj was tried here and found (by a real `podman build`, not just reasoning
# about it) to fail with NU1102 hunting for a nonexistent "Microsoft.NETCore.App.Runtime.Mono.linux-x64"
# package - NuGet's restore-graph walk visits every ProjectReference regardless of
# ReferenceOutputAssembly, so the explicit "-r linux-x64" got force-applied to the Gui.Web WASM sibling
# too (which must always restore for its own browser-wasm target, never the outer RID). `dotnet publish
# -r linux-x64` in one shot - verified for real, both here and as a plain `dotnet publish` outside any
# container during this phase's own local verification - does not hit this, because its restore honors
# each project's own target/RID rather than a single CLI-wide override. The cost is losing this layer's
# restore-result caching across source-only rebuilds; correctness won over that here.
COPY src/ src/
RUN dotnet publish src/TotallyHotArcRouter/TotallyHotArcRouter.csproj \
    -c Release -r linux-x64 --self-contained false -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Non-root: matches every other install path's posture (packaging/linux and packaging/macos also run
# the router itself as a dedicated unprivileged account - only the one-time cert-trust step in each
# runs elevated, and there is no equivalent OS trust store to touch inside a container).
RUN groupadd --system arcrouter \
    && useradd --system --no-create-home --gid arcrouter arcrouter \
    && mkdir -p /data \
    && chown -R arcrouter:arcrouter /data

COPY --from=build /app/publish .
RUN chown -R arcrouter:arcrouter /app

USER arcrouter

# AppDataPaths.ResolveMachineSharedDirectory() checks STATE_DIRECTORY unconditionally (not only under
# systemd - see its remarks), so pointing it at the mounted volume is enough to relocate every piece of
# router state (secrets, the local CA, SQLite databases, trained model artifacts, and - via
# ResolveLogsDirectory()'s own fallback - logs under /data/logs) without any other environment variable.
ENV STATE_DIRECTORY=/data
# Widened for the Docker case: "loopback" inside the container's own network namespace is not reachable
# from the host at all without this. Still requires trusting the local CA and/or `docker run -p` mapping
# only to 127.0.0.1 on the host, same as any other loopback-only-by-default service.
ENV WebInterface__BindAddress=0.0.0.0
ENV Proxy__BindAddress=0.0.0.0
ENV Mcp__BindAddress=0.0.0.0

EXPOSE 47101 47103 47104

ENTRYPOINT ["dotnet", "TotallyHotArcRouter.dll"]
