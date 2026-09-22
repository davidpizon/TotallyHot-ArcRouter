# Security Policy

## Supported Versions

This project cuts versioned GitHub Releases from `vMAJOR.MINOR.PATCH` tags.
[`.github/workflows/release.yml`](.github/workflows/release.yml) publishes the Windows MSI,
platform tarballs (`linux-x64`, `linux-arm64`, `osx-arm64`), and the GHCR image;
[`.github/workflows/promote.yml`](.github/workflows/promote.yml) nominates a prerelease to
`latest`. See
[`docs/router/packaging-and-distribution.md`](docs/router/packaging-and-distribution.md) §7.

Security fixes land on `main` and ship in the next tagged release. There is no commitment to
backport fixes onto older release lines. The currently promoted (`latest`) release is the
supported install. Releases marked **Pre-release** are release candidates — they are built by the
same pipeline but are not the support target, and the in-product update check never offers them.

## Reporting a Vulnerability

Please report suspected vulnerabilities privately via
[GitHub's private vulnerability reporting](https://github.com/davidpizon/TotallyHot-ArcRouter/security/advisories/new)
rather than filing a public issue. Include reproduction steps and the
affected file/commit where possible.

There is no formal SLA — this is a personal project maintained by one
person — but reports will be acknowledged and triaged as soon as
practical, and a fix or mitigation will be prioritized once a report is
confirmed.
