#!/usr/bin/env python3
"""File nightly audit GitHub issues. Never opens a pull request or pushes code.

Vulnerability findings (NuGet advisories, and Qodana rules in a Security category) are
deduplicated by a stable fingerprint stored in the issue body. Code-smell findings are
Qodana results that are new against the committed baseline; a fingerprint that already
has an issue, open or closed, is not filed again.

The scan comments once when an open issue's finding disappears, and once when a closed
issue's finding is reported again. It does not close or reopen issues.

Run ``python3 .github/scripts/nightly_audit.py --self-test`` to check the planner
against sample findings without calling GitHub.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Mapping, Sequence

API = "https://api.github.com"
FINGERPRINT_RE = re.compile(r"<!-- audit-fingerprint: ([A-Za-z0-9:._~/#-]+) -->")
ABSENT_MARKER = "<!-- audit-absent: true -->"
REGRESSED_MARKER = "<!-- audit-regressed: true -->"
NUGET_PREFIX = "vuln:nuget:"
QODANA_SMELL_PREFIX = "smell:qodana:"
QODANA_VULN_PREFIX = "vuln:qodana:"
SEVERITY_RANK = {"critical": 0, "high": 1, "moderate": 2, "low": 3, "info": 4}
SEVERITY_ALIASES = {
    "medium": "moderate",
    "important": "high",
    "error": "high",
    "warning": "moderate",
    "note": "info",
}
LABELS = {
    "audit:vulnerability": (
        "b60205",
        "Nightly audit: dependency or code vulnerability. Filing is not approval to code.",
    ),
    "audit:smell": (
        "fbca04",
        "Nightly audit: Qodana finding that is new versus the committed baseline.",
    ),
    "severity:critical": ("b60205", "Audit finding severity: critical."),
    "severity:high": ("d93f0b", "Audit finding severity: high."),
    "severity:moderate": ("fbca04", "Audit finding severity: moderate."),
    "severity:low": ("0e8a16", "Audit finding severity: low."),
    "severity:info": ("5319e7", "Audit finding severity: info."),
}
PLAN_LINK = "docs/router/standing-rules.md"
AMENDMENT_LINK = (
    "docs/adr/0008-codegraph-serena-dual-engine-code-smell-pipeline.md"
    "#amendment-2-2026-10-07-scheduled-audits-that-do-not-churn"
)
RULE1_LINK = (
    "docs/adr/0008-codegraph-serena-dual-engine-code-smell-pipeline.md"
    "#amendment-1-2026-09-02-stop-rules"
)


@dataclass(frozen=True)
class Finding:
    """One distinct audit result, already deduplicated within a single scan."""

    fingerprint: str
    kind: str
    severity: str
    title: str
    details: str

    def body(self) -> str:
        return f"<!-- audit-fingerprint: {self.fingerprint} -->\n\n{self.details.rstrip()}\n"

    def managed_labels(self) -> set[str]:
        audit = "audit:vulnerability" if self.kind == "vulnerability" else "audit:smell"
        return {audit, f"severity:{self.severity}"}


@dataclass(frozen=True)
class IssueSnapshot:
    """An existing issue the planner can see, including the comment bodies it may need."""

    number: int
    state: str
    title: str
    body: str
    labels: frozenset[str]
    comments: tuple[str, ...] = ()


@dataclass(frozen=True)
class Planned:
    """One GitHub write, or an explicit skip so a dry run can show why nothing happened."""

    op: str
    fingerprint: str
    number: int | None = None
    title: str = ""
    body: str = ""
    labels: tuple[str, ...] = ()
    comment: str = ""


def validate_fingerprint(fingerprint: str) -> str:
    """Reject a fingerprint the next run would fail to read back out of the issue body."""
    if not re.fullmatch(r"[A-Za-z0-9:._~/#-]+", fingerprint):
        raise ValueError(f"fingerprint has unsupported characters: {fingerprint}")
    return fingerprint


def extract_fingerprint(body: str | None) -> str | None:
    if not body:
        return None
    match = FINGERPRINT_RE.search(body)
    return match.group(1) if match else None


def normalize_severity(raw: str | None) -> str:
    key = SEVERITY_ALIASES.get((raw or "").strip().lower(), (raw or "").strip().lower())
    return key if key in SEVERITY_RANK else "info"


def normalize_text(text: str | None) -> str:
    return (text or "").replace("\r\n", "\n").strip()


def clip(text: str, limit: int = 240) -> str:
    compact = " ".join(text.split())
    if len(compact) <= limit:
        return compact
    return compact[: limit - 1] + "…"


def load_json_text(text: str) -> object:
    """Parse a JSON object, ignoring any preamble a tool wrote ahead of it."""
    start = text.find("{")
    if start < 0:
        raise ValueError("no JSON object in input")
    value, _ = json.JSONDecoder().raw_decode(text[start:])
    return value


def canonical_advisory(url: str) -> str:
    parts = urllib.parse.urlsplit(url.strip())
    path = parts.path.rstrip("/") or "/"
    return urllib.parse.urlunsplit((parts.scheme.lower(), parts.netloc.lower(), path, "", "")).lower()


def nuget_fingerprint(package_id: str, advisory: str) -> str:
    package = package_id.strip().lower()
    if not re.fullmatch(r"[a-z0-9._-]+", package):
        package = "id-" + hashlib.sha256(package_id.encode()).hexdigest()[:16]
    advisory_part = canonical_advisory(advisory) if advisory else "no-advisory"
    return validate_fingerprint(f"{NUGET_PREFIX}{package}:{advisory_part}")


def qodana_fingerprint(kind: str, indicator: str) -> str:
    prefix = QODANA_VULN_PREFIX if kind == "vulnerability" else QODANA_SMELL_PREFIX
    if not re.fullmatch(r"[A-Fa-f0-9]+", indicator):
        indicator = hashlib.sha256(indicator.encode()).hexdigest()
    return validate_fingerprint(prefix + indicator.lower())


def relative_project(path: str) -> str:
    normalized = path.replace("\\", "/")
    index = normalized.lower().rfind("/src/")
    if index >= 0:
        return normalized[index + 1 :]
    if normalized.lower().startswith("src/"):
        return normalized
    return normalized


def repo_path(uri: str, uri_base: str | None) -> str:
    """Map a Qodana artifact URI onto a repository-relative path.

    The committed baseline stores paths relative to the solution directory
    (``src/``), which is what ``uriBaseId: solutionDir`` means. Prefix that
    base so an issue names a file a person can open.
    """
    text = urllib.parse.unquote(uri or "")
    for root in (
        "file:///data/project/src/",
        "/data/project/src/",
        "file:///data/project/",
        "/data/project/",
    ):
        if text.startswith(root):
            text = text[len(root) :]
            break
    if uri_base == "solutionDir" and text and not text.startswith("src/"):
        text = "src/" + text
    return text


def _packages(framework: Mapping[str, object]) -> Iterable[Mapping[str, object]]:
    for key in ("topLevelPackages", "transitivePackages"):
        packages = framework.get(key) or []
        if isinstance(packages, list):
            for package in packages:
                if isinstance(package, Mapping):
                    yield package


def _advisory_url(vulnerability: Mapping[str, object]) -> str:
    for key in ("advisoryurl", "advisoryUrl", "advisoryURL"):
        value = vulnerability.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return ""


def parse_nuget_documents(documents: Sequence[Mapping[str, object]]) -> list[Finding]:
    """Collapse vulnerable packages across projects into one finding per advisory."""
    grouped: dict[str, list[dict[str, str]]] = {}
    for document in documents:
        for project in document.get("projects") or []:
            if not isinstance(project, Mapping):
                continue
            project_path = relative_project(str(project.get("path") or "unknown project"))
            for framework in project.get("frameworks") or []:
                if not isinstance(framework, Mapping):
                    continue
                for package in _packages(framework):
                    package_id = str(package.get("id") or "").strip()
                    if not package_id:
                        continue
                    version = str(package.get("resolvedVersion") or package.get("requestedVersion") or "").strip()
                    vulnerabilities = package.get("vulnerabilities") or []
                    if not isinstance(vulnerabilities, list):
                        continue
                    for vulnerability in vulnerabilities:
                        if not isinstance(vulnerability, Mapping):
                            continue
                        advisory = _advisory_url(vulnerability)
                        fingerprint = nuget_fingerprint(package_id, advisory)
                        grouped.setdefault(fingerprint, []).append(
                            {
                                "id": package_id,
                                "version": version or "unknown",
                                "advisory": advisory,
                                "project": project_path,
                                "severity": normalize_severity(str(vulnerability.get("severity") or "")),
                            }
                        )

    findings: list[Finding] = []
    for fingerprint, hits in sorted(grouped.items()):
        severity = min(hits, key=lambda hit: SEVERITY_RANK[hit["severity"]])["severity"]
        package_id = hits[0]["id"]
        advisory = hits[0]["advisory"] or "(no advisory URL)"
        versions = ", ".join(sorted({hit["version"] for hit in hits}))
        projects = "\n".join(f"- `{hit['project']}` ({hit['version']})" for hit in sorted(hits, key=lambda hit: (hit["project"], hit["version"])))
        advisory_name = advisory.rstrip("/").rsplit("/", 1)[-1] if advisory.startswith("http") else "advisory"
        title = clip(f"[vuln] {package_id} {versions} — {advisory_name}")
        details = "\n".join(
            [
                "This issue was opened by the nightly dependency audit",
                "([`.github/workflows/nightly-audit.yml`](.github/workflows/nightly-audit.yml)).",
                "The workflow does not open a pull request or change code.",
                f"A fix needs an approved plan under [`{PLAN_LINK}`]({PLAN_LINK})",
                f"before coding ([ADR-0008 Amendment 2]({AMENDMENT_LINK})).",
                "A vulnerability fix's observed cost is the vulnerability itself.",
                "Dependabot may also open its own security pull request for the same advisory;",
                "this workflow does not.",
                "",
                "Leave the fingerprint comment at the top of this issue in place so the next",
                "run updates this issue instead of opening another one.",
                "",
                "| | |",
                "|---|---|",
                "| Kind | Vulnerability (NuGet) |",
                f"| Severity | {severity} |",
                f"| Package | `{package_id}` |",
                f"| Resolved version | {versions} |",
                f"| Advisory | {advisory} |",
                "",
                "Projects:",
                "",
                projects,
            ]
        )
        findings.append(
            Finding(
                fingerprint=fingerprint,
                kind="vulnerability",
                severity=severity,
                title=title,
                details=details,
            )
        )
    return findings


def parse_nuget_directory(directory: Path) -> list[Finding]:
    files = sorted(directory.glob("*.json"))
    if not files:
        raise ValueError(f"no NuGet audit JSON in {directory}")
    documents = []
    for path in files:
        parsed = load_json_text(path.read_text(encoding="utf-8-sig"))
        if not isinstance(parsed, Mapping):
            raise ValueError(f"{path} is not a JSON object")
        documents.append(parsed)
    return parse_nuget_documents(documents)


def _result_text(result: Mapping[str, object]) -> tuple[str, str, str, int]:
    message = result.get("message")
    message_text = ""
    if isinstance(message, Mapping):
        message_text = str(message.get("text") or message.get("markdown") or "")
    locations = result.get("locations") or []
    path = ""
    snippet = ""
    line = 0
    if isinstance(locations, list) and locations and isinstance(locations[0], Mapping):
        physical = locations[0].get("physicalLocation")
        if isinstance(physical, Mapping):
            artifact = physical.get("artifactLocation")
            if isinstance(artifact, Mapping):
                path = repo_path(str(artifact.get("uri") or ""), str(artifact.get("uriBaseId") or "") or None)
            region = physical.get("region")
            if isinstance(region, Mapping):
                line = int(region.get("startLine") or 0)
                snippet_node = region.get("snippet")
                if isinstance(snippet_node, Mapping):
                    snippet = str(snippet_node.get("text") or "")
    return path, message_text, snippet[:500], line


def result_indicator(result: Mapping[str, object], path: str, message: str, snippet: str) -> str:
    fingerprints = result.get("partialFingerprints")
    if isinstance(fingerprints, Mapping):
        raw = fingerprints.get("equalIndicator/v1")
        if isinstance(raw, str) and raw.strip():
            return raw.strip()
    material = f"{result.get('ruleId')}\n{path}\n{message}\n{snippet}"
    return hashlib.sha256(material.encode()).hexdigest()


def baseline_state(result: Mapping[str, object]) -> str:
    state = result.get("baselineState")
    if not state:
        properties = result.get("properties")
        if isinstance(properties, Mapping):
            state = properties.get("baselineState") or ""
    return str(state or "").lower()


def security_rule_ids(run: Mapping[str, object]) -> set[str]:
    tool = run.get("tool")
    driver = tool.get("driver") if isinstance(tool, Mapping) else None
    rules = driver.get("rules") if isinstance(driver, Mapping) else None
    found: set[str] = set()
    if not isinstance(rules, list):
        return found
    for rule in rules:
        if not isinstance(rule, Mapping):
            continue
        rule_id = str(rule.get("id") or "")
        targets: list[str] = []
        for relationship in rule.get("relationships") or []:
            if isinstance(relationship, Mapping):
                target = relationship.get("target")
                if isinstance(target, Mapping):
                    targets.append(str(target.get("id") or ""))
        if "Security" in rule_id or any("Security" in target for target in targets):
            found.add(rule_id)
    return found


def _rule_help(run: Mapping[str, object], rule_id: str) -> str:
    tool = run.get("tool")
    driver = tool.get("driver") if isinstance(tool, Mapping) else None
    rules = driver.get("rules") if isinstance(driver, Mapping) else None
    if not isinstance(rules, list):
        return ""
    for rule in rules:
        if isinstance(rule, Mapping) and rule.get("id") == rule_id:
            help_uri = rule.get("helpUri")
            return str(help_uri) if isinstance(help_uri, str) else ""
    return ""


def _severity_of(result: Mapping[str, object]) -> str:
    properties = result.get("properties")
    if isinstance(properties, Mapping) and properties.get("qodanaSeverity"):
        return normalize_severity(str(properties.get("qodanaSeverity")))
    level = str(result.get("level") or "").lower()
    return {"error": "high", "warning": "moderate", "note": "low", "none": "info"}.get(level, "info")


def indicators_in(run: Mapping[str, object]) -> set[str]:
    found: set[str] = set()
    for result in run.get("results") or []:
        if isinstance(result, Mapping):
            path, message, snippet, _line = _result_text(result)
            found.add(result_indicator(result, path, message, snippet))
    return found


def scan_is_complete(sarif: Mapping[str, object]) -> bool:
    """A partial report must not be treated as 'every previous finding is gone'."""
    runs = sarif.get("runs")
    if not isinstance(runs, list) or not runs or not isinstance(runs[0], Mapping):
        raise ValueError("SARIF has no runs")
    if not isinstance(runs[0].get("results"), list):
        raise ValueError("SARIF run has no results array")
    invocations = runs[0].get("invocations") or []
    if not isinstance(invocations, list) or not invocations:
        return True
    return all(isinstance(item, Mapping) and item.get("executionSuccessful") is True for item in invocations)


def first_run(sarif: Mapping[str, object]) -> Mapping[str, object]:
    runs = sarif.get("runs")
    if not isinstance(runs, list) or not runs or not isinstance(runs[0], Mapping):
        raise ValueError("SARIF has no runs")
    return runs[0]


def parse_qodana(sarif: Mapping[str, object], baseline: Mapping[str, object] | None) -> tuple[list[Finding], bool]:
    """Split a Qodana report into vulnerability and new-smell findings.

    Security-category results are vulnerabilities even when the baseline already
    contains them. Every other result is a smell only when Qodana marks it new,
    or, if the report has no baseline state, when its indicator is not in the
    baseline file. Unchanged and updated smells are the catalog Amendment 2
    refuses to re-file.
    """
    complete = scan_is_complete(sarif)
    run = first_run(sarif)
    secure = security_rule_ids(run)
    known = indicators_in(first_run(baseline)) if baseline is not None else set()
    findings: list[Finding] = []
    seen: set[str] = set()
    for result in run.get("results") or []:
        if not isinstance(result, Mapping):
            continue
        if result.get("kind") in {"pass", "notApplicable"}:
            continue
        state = baseline_state(result)
        if state == "absent":
            continue
        rule_id = str(result.get("ruleId") or "unknown")
        path, message, snippet, line = _result_text(result)
        indicator = result_indicator(result, path, message, snippet)
        security = rule_id in secure
        if security:
            kind = "vulnerability"
        elif state == "new":
            kind = "smell"
        elif state == "":
            if indicator in known:
                continue
            kind = "smell"
        else:
            continue
        fingerprint = qodana_fingerprint(kind, indicator)
        if fingerprint in seen:
            continue
        seen.add(fingerprint)
        severity = _severity_of(result)
        filename = path.rsplit("/", 1)[-1] if path else rule_id
        prefix = "[vuln]" if kind == "vulnerability" else "[smell]"
        title = clip(f"{prefix} {rule_id} — {filename}")
        location = f"`{path}`" if path else "(no path)"
        if line:
            location += f" line {line}"
        help_uri = _rule_help(run, rule_id)
        help_line = f"| Help | {help_uri} |" if help_uri else ""
        if kind == "vulnerability":
            intro = [
                "This issue was opened by the nightly code vulnerability audit",
                "(Qodana rules in a Security category).",
                "The workflow does not open a pull request or change code.",
                f"A fix needs an approved plan under [`{PLAN_LINK}`]({PLAN_LINK})",
                f"before coding ([ADR-0008 Amendment 2]({AMENDMENT_LINK})).",
                "A vulnerability fix's observed cost is the vulnerability itself.",
            ]
            kind_label = "Vulnerability (Qodana security inspection)"
        else:
            intro = [
                "This issue was opened by the nightly code-smell audit because the finding",
                "is new against `.qodana/qodana.sarif.json`.",
                "The workflow does not open a pull request or change code.",
                f"A fix needs an approved plan under [`{PLAN_LINK}`]({PLAN_LINK})",
                f"before coding ([ADR-0008 Amendment 2]({AMENDMENT_LINK})).",
                "A smell fix still needs an observed cost",
                f"([ADR-0008 Amendment 1]({RULE1_LINK}) rule 1).",
                "Filing this issue is not that citation and it is not plan approval.",
            ]
            kind_label = "Code smell (Qodana, new versus the committed baseline)"
        snippet_block = f"\n```\n{snippet.rstrip()}\n```\n" if snippet.strip() else ""
        details = "\n".join(
            [
                *intro,
                "",
                "Leave the fingerprint comment at the top of this issue in place so the next",
                "run updates this issue instead of opening another one.",
                "",
                "| | |",
                "|---|---|",
                f"| Kind | {kind_label} |",
                f"| Rule | `{rule_id}` |",
                f"| Severity | {severity} |",
                f"| Location | {location} |",
                help_line,
                "",
                message or "(no message)",
                snippet_block,
            ]
        )
        findings.append(
            Finding(
                fingerprint=fingerprint,
                kind=kind,
                severity=severity,
                title=title,
                details="\n".join(line for line in details.split("\n")),
            )
        )
    return findings, complete


def absent_comment(run_url: str) -> str:
    where = run_url or "(run URL not recorded)"
    return (
        f"{ABSENT_MARKER}\n\n"
        "The nightly audit no longer reports this finding"
        f" ([run]({where})). This issue was left open on purpose. "
        "Close it by hand if the fix has landed. The workflow will not close it "
        "and it will not open a pull request.\n"
    )


def regressed_comment(run_url: str) -> str:
    where = run_url or "(run URL not recorded)"
    return (
        f"{REGRESSED_MARKER}\n\n"
        "The nightly audit reports this finding again"
        f" ([run]({where})). This issue stays closed. Reopen it by hand if it should "
        "be tracked. The workflow will not open a second issue and will not open a pull request.\n"
    )


def plan_actions(
    findings: Sequence[Finding],
    issues: Sequence[IssueSnapshot],
    scope_prefixes: Sequence[str],
    run_url: str,
    allow_absent: bool,
) -> list[Planned]:
    """Decide creates, updates, skips, and one-time comments. Does not call GitHub."""
    by_fingerprint: dict[str, IssueSnapshot] = {}
    for issue in issues:
        fingerprint = extract_fingerprint(issue.body)
        if fingerprint is None or not any(fingerprint.startswith(prefix) for prefix in scope_prefixes):
            continue
        previous = by_fingerprint.get(fingerprint)
        if previous is None:
            by_fingerprint[fingerprint] = issue
            continue
        previous_open = previous.state == "open"
        issue_open = issue.state == "open"
        if issue_open and not previous_open:
            by_fingerprint[fingerprint] = issue
        elif issue_open == previous_open and issue.number < previous.number:
            by_fingerprint[fingerprint] = issue

    finding_by_fingerprint: dict[str, Finding] = {}
    for finding in findings:
        if not any(finding.fingerprint.startswith(prefix) for prefix in scope_prefixes):
            raise ValueError(f"finding {finding.fingerprint} is outside {scope_prefixes}")
        finding_by_fingerprint[finding.fingerprint] = finding

    planned: list[Planned] = []
    for fingerprint, finding in sorted(finding_by_fingerprint.items()):
        issue = by_fingerprint.get(fingerprint)
        desired_body = finding.body()
        if issue is None:
            planned.append(
                Planned(
                    "create",
                    fingerprint,
                    title=finding.title,
                    body=desired_body,
                    labels=tuple(sorted(finding.managed_labels())),
                )
            )
            continue
        if issue.state != "open":
            if any(REGRESSED_MARKER in comment for comment in issue.comments):
                planned.append(Planned("skip", fingerprint, number=issue.number))
            else:
                planned.append(
                    Planned(
                        "comment-regressed",
                        fingerprint,
                        number=issue.number,
                        comment=regressed_comment(run_url),
                    )
                )
            continue
        kept = {label for label in issue.labels if not label.startswith(("audit:", "severity:"))}
        new_labels = kept | finding.managed_labels()
        same = (
            issue.title == finding.title
            and normalize_text(issue.body) == normalize_text(desired_body)
            and set(issue.labels) == new_labels
        )
        if same:
            planned.append(Planned("skip", fingerprint, number=issue.number))
        else:
            planned.append(
                Planned(
                    "update",
                    fingerprint,
                    number=issue.number,
                    title=finding.title,
                    body=desired_body,
                    labels=tuple(sorted(new_labels)),
                )
            )

    if allow_absent:
        for fingerprint, issue in sorted(by_fingerprint.items()):
            if fingerprint in finding_by_fingerprint or issue.state != "open":
                continue
            if any(ABSENT_MARKER in comment for comment in issue.comments):
                planned.append(Planned("skip", fingerprint, number=issue.number))
            else:
                planned.append(
                    Planned(
                        "comment-absent",
                        fingerprint,
                        number=issue.number,
                        comment=absent_comment(run_url),
                    )
                )
    return planned


def summarize(actions: Sequence[Planned]) -> str:
    counts = Counter(action.op for action in actions)
    return (
        f"created={counts['create']} updated={counts['update']} skipped={counts['skip']} "
        f"absent={counts['comment-absent']} regressed={counts['comment-regressed']}"
    )


def action_payload(action: Planned) -> dict[str, object]:
    payload: dict[str, object] = {"op": action.op, "fingerprint": action.fingerprint}
    if action.number is not None:
        payload["number"] = action.number
    if action.title:
        payload["title"] = action.title
    if action.labels:
        payload["labels"] = list(action.labels)
    if action.comment:
        payload["comment"] = action.comment
    return payload


class GitHub:
    """Minimal Issues API client. The token is the job's GITHUB_TOKEN."""

    def __init__(self, repo: str, token: str, dry_run: bool = False) -> None:
        if repo.count("/") != 1:
            raise ValueError(f"repo must be owner/name, got {repo!r}")
        self.repo = repo
        self.token = token
        self.dry_run = dry_run

    def _request(self, method: str, url: str, body: Mapping[str, object] | None = None) -> tuple[object, str]:
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(url, data=data, method=method)
        request.add_header("Authorization", f"Bearer {self.token}")
        request.add_header("Accept", "application/vnd.github+json")
        request.add_header("X-GitHub-Api-Version", "2022-11-28")
        request.add_header("User-Agent", "totallyhot-nightly-audit")
        if body is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                raw = response.read().decode()
                parsed = json.loads(raw) if raw else None
                return parsed, response.headers.get("Link") or ""
        except urllib.error.HTTPError as error:
            detail = error.read().decode(errors="replace")
            raise RuntimeError(f"GitHub {method} {url} failed: HTTP {error.code} {detail}") from error

    def _paginate(self, url: str) -> list[dict[str, object]]:
        items: list[dict[str, object]] = []
        while url:
            parsed, link = self._request("GET", url)
            if not isinstance(parsed, list):
                raise RuntimeError(f"expected a list from {url}")
            items.extend(item for item in parsed if isinstance(item, dict))
            url = next_link(link) or ""
        return items

    def ensure_label(self, name: str, color: str, description: str) -> None:
        if self.dry_run:
            return
        quoted = urllib.parse.quote(name, safe="")
        try:
            self._request("GET", f"{API}/repos/{self.repo}/labels/{quoted}")
            return
        except RuntimeError as error:
            if "HTTP 404" not in str(error):
                raise
        try:
            self._request(
                "POST",
                f"{API}/repos/{self.repo}/labels",
                {"name": name, "color": color, "description": description},
            )
        except RuntimeError as error:
            # The dependency job and the Qodana job create the same severity
            # labels in parallel. The loser sees the label the winner just made.
            if "HTTP 422" not in str(error):
                raise

    def list_issues(self, label: str) -> list[dict[str, object]]:
        quoted = urllib.parse.quote(label, safe="")
        url = f"{API}/repos/{self.repo}/issues?state=all&labels={quoted}&per_page=100"
        return [item for item in self._paginate(url) if "pull_request" not in item]

    def list_comments(self, number: int) -> list[str]:
        url = f"{API}/repos/{self.repo}/issues/{number}/comments?per_page=100"
        bodies: list[str] = []
        for item in self._paginate(url):
            body = item.get("body")
            if isinstance(body, str):
                bodies.append(body)
        return bodies

    def create_issue(self, title: str, body: str, labels: Sequence[str]) -> int:
        if self.dry_run:
            return 0
        parsed, _link = self._request(
            "POST",
            f"{API}/repos/{self.repo}/issues",
            {"title": title, "body": body, "labels": list(labels)},
        )
        if not isinstance(parsed, Mapping) or not isinstance(parsed.get("number"), int):
            raise RuntimeError("GitHub did not return an issue number")
        return int(parsed["number"])

    def update_issue(self, number: int, title: str, body: str, labels: Sequence[str]) -> None:
        if self.dry_run:
            return
        self._request(
            "PATCH",
            f"{API}/repos/{self.repo}/issues/{number}",
            {"title": title, "body": body, "labels": list(labels)},
        )

    def create_comment(self, number: int, body: str) -> None:
        if self.dry_run:
            return
        self._request(
            "POST",
            f"{API}/repos/{self.repo}/issues/{number}/comments",
            {"body": body},
        )


def next_link(header: str | None) -> str | None:
    if not header:
        return None
    for part in header.split(","):
        if 'rel="next"' not in part:
            continue
        match = re.search(r"<([^>]+)>", part)
        if match:
            return match.group(1)
    return None


def snapshots_from_issues(github: GitHub, labels: Sequence[str], prefixes: Sequence[str], finding_fingerprints: set[str]) -> list[IssueSnapshot]:
    """Load issues for these labels. Comments are fetched only when a comment decision needs them."""
    seen: set[int] = set()
    snapshots: list[IssueSnapshot] = []
    for label in labels:
        for item in github.list_issues(label):
            number = item.get("number")
            if not isinstance(number, int) or number in seen:
                continue
            body = item.get("body") if isinstance(item.get("body"), str) else ""
            fingerprint = extract_fingerprint(body)
            if fingerprint is None or not any(fingerprint.startswith(prefix) for prefix in prefixes):
                continue
            seen.add(number)
            state = str(item.get("state") or "")
            label_names = {
                str(label_item.get("name"))
                for label_item in item.get("labels") or []
                if isinstance(label_item, Mapping) and label_item.get("name")
            }
            needs_comments = state != "open" or fingerprint not in finding_fingerprints
            comments = tuple(github.list_comments(number)) if needs_comments else ()
            snapshots.append(
                IssueSnapshot(
                    number=number,
                    state=state,
                    title=str(item.get("title") or ""),
                    body=body,
                    labels=frozenset(label_names),
                    comments=comments,
                )
            )
    return snapshots


def load_snapshots_file(path: Path) -> list[IssueSnapshot]:
    parsed = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(parsed, list):
        raise ValueError(f"{path} must be a JSON list")
    snapshots: list[IssueSnapshot] = []
    for item in parsed:
        if not isinstance(item, Mapping):
            raise ValueError(f"{path} contains a non-object issue")
        snapshots.append(
            IssueSnapshot(
                number=int(item["number"]),
                state=str(item.get("state") or "open"),
                title=str(item.get("title") or ""),
                body=str(item.get("body") or ""),
                labels=frozenset(str(label) for label in item.get("labels") or []),
                comments=tuple(str(comment) for comment in item.get("comments") or []),
            )
        )
    return snapshots


def apply_plan(github: GitHub, actions: Sequence[Planned], dry_run: bool) -> None:
    for action in actions:
        print(json.dumps(action_payload(action), sort_keys=True))
        if dry_run or action.op == "skip":
            continue
        if action.op == "create":
            number = github.create_issue(action.title, action.body, action.labels)
            print(f"created issue #{number} for {action.fingerprint}")
        elif action.op == "update":
            if action.number is None:
                raise ValueError("update without an issue number")
            github.update_issue(action.number, action.title, action.body, action.labels)
        elif action.op in {"comment-absent", "comment-regressed"}:
            if action.number is None:
                raise ValueError("comment without an issue number")
            github.create_comment(action.number, action.comment)
        else:
            raise ValueError(f"unknown plan op {action.op}")


def ensure_labels(github: GitHub, findings: Sequence[Finding]) -> None:
    needed: set[str] = set()
    for finding in findings:
        needed |= finding.managed_labels()
    for name in sorted(needed):
        color, description = LABELS[name]
        github.ensure_label(name, color, description)


def sync(
    github: GitHub | None,
    findings: Sequence[Finding],
    issues: Sequence[IssueSnapshot],
    prefixes: Sequence[str],
    labels: Sequence[str],
    run_url: str,
    allow_absent: bool,
    dry_run: bool,
) -> list[Planned]:
    if github is not None and not issues:
        issues = snapshots_from_issues(
            github,
            labels,
            prefixes,
            {finding.fingerprint for finding in findings},
        )
    actions = plan_actions(findings, issues, prefixes, run_url, allow_absent)
    if github is None:
        github = GitHub("local/dry-run", token="", dry_run=True)
    ensure_labels(github, findings)
    apply_plan(github, actions, dry_run or github.dry_run)
    print(summarize(actions))
    return actions


def _add_common(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    parser.add_argument("--run-url", default=os.environ.get("AUDIT_RUN_URL", ""))
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--existing-issues", type=Path)


def command_sync_nuget(args: argparse.Namespace) -> int:
    findings = parse_nuget_directory(args.input_dir)
    return _run_sync(
        args,
        findings,
        prefixes=(NUGET_PREFIX,),
        labels=("audit:vulnerability",),
        allow_absent=True,
    )


def command_sync_sarif(args: argparse.Namespace) -> int:
    sarif_text = args.sarif.read_text(encoding="utf-8-sig")
    sarif = load_json_text(sarif_text)
    if not isinstance(sarif, Mapping):
        raise ValueError(f"{args.sarif} is not a JSON object")
    baseline = None
    if args.baseline is not None:
        parsed = load_json_text(args.baseline.read_text(encoding="utf-8-sig"))
        if not isinstance(parsed, Mapping):
            raise ValueError(f"{args.baseline} is not a JSON object")
        baseline = parsed
    findings, complete = parse_qodana(sarif, baseline)
    code = _run_sync(
        args,
        findings,
        prefixes=(QODANA_SMELL_PREFIX, QODANA_VULN_PREFIX),
        labels=("audit:smell", "audit:vulnerability"),
        allow_absent=complete,
    )
    if not complete:
        print("Qodana report is incomplete; filed current results and did not comment on disappearances", file=sys.stderr)
        return 1
    return code


def _run_sync(
    args: argparse.Namespace,
    findings: Sequence[Finding],
    prefixes: Sequence[str],
    labels: Sequence[str],
    allow_absent: bool,
) -> int:
    if args.existing_issues is not None:
        issues = load_snapshots_file(args.existing_issues)
        github = None
    else:
        token = os.environ.get("GITHUB_TOKEN", "")
        if not token and not args.dry_run:
            raise SystemExit("GITHUB_TOKEN is required unless --dry-run is combined with --existing-issues")
        if not args.repo:
            raise SystemExit("--repo or GITHUB_REPOSITORY is required")
        issues = []
        github = GitHub(args.repo, token, dry_run=args.dry_run) if token else None
        if github is None:
            raise SystemExit("GITHUB_TOKEN is required to read existing issues")
    sync(
        github,
        findings,
        issues,
        prefixes,
        labels,
        args.run_url,
        allow_absent,
        args.dry_run or github is None,
    )
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="File nightly audit issues. Does not open pull requests.")
    sub = parser.add_subparsers(dest="command", required=True)

    nuget = sub.add_parser("sync-nuget", help="File NuGet vulnerability issues from dotnet list JSON")
    nuget.add_argument("--input-dir", type=Path, required=True)
    _add_common(nuget)
    nuget.set_defaults(func=command_sync_nuget)

    sarif = sub.add_parser("sync-sarif", help="File new Qodana smell issues and Qodana security issues")
    sarif.add_argument("--sarif", type=Path, required=True)
    sarif.add_argument("--baseline", type=Path)
    _add_common(sarif)
    sarif.set_defaults(func=command_sync_sarif)
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return int(args.func(args))


# --- self-test -----------------------------------------------------------------

def _nuget_document(path: str, package: str, version: str, severity: str, advisory: str) -> dict[str, object]:
    return {
        "projects": [
            {
                "path": path,
                "frameworks": [
                    {
                        "framework": "net10.0",
                        "topLevelPackages": [
                            {
                                "id": package,
                                "resolvedVersion": version,
                                "vulnerabilities": [{"severity": severity, "advisoryurl": advisory}],
                            }
                        ],
                    }
                ],
            }
        ]
    }


def _qodana_result(
    rule: str,
    indicator: str,
    state: str | None = None,
    message: str = "captured variable",
    path: str = "TotallyHotArcRouter/Proxy/Foo.cs",
    severity: str = "High",
) -> dict[str, object]:
    item: dict[str, object] = {
        "ruleId": rule,
        "kind": "fail",
        "level": "warning",
        "message": {"text": message},
        "partialFingerprints": {"equalIndicator/v1": indicator},
        "locations": [
            {
                "physicalLocation": {
                    "artifactLocation": {"uri": path, "uriBaseId": "solutionDir"},
                    "region": {"startLine": 12, "snippet": {"text": "stopCts"}},
                }
            }
        ],
        "properties": {"qodanaSeverity": severity},
    }
    if state is not None:
        item["baselineState"] = state
    return item


def _sarif(results: list[dict[str, object]], rules: list[dict[str, object]], successful: bool = True) -> dict[str, object]:
    return {
        "version": "2.1.0",
        "runs": [
            {
                "tool": {"driver": {"name": "QDNETC", "rules": rules}},
                "invocations": [{"executionSuccessful": successful}],
                "results": results,
            }
        ],
    }


SMELL_RULE = {"id": "AccessToDisposedClosure", "relationships": [{"target": {"id": "CSHARP.CodeSmell"}, "kinds": ["superset"]}]}
SECURITY_RULE = {
    "id": "VulnerableApi",
    "helpUri": "https://www.jetbrains.com/help/resharper/VulnerableApi.html",
    "relationships": [{"target": {"id": "CSHARP.Security"}, "kinds": ["superset"]}],
}


def _run_self_test() -> int:
    import subprocess
    import tempfile
    import unittest

    class NightlyAuditTests(unittest.TestCase):
        def test_nuget_groups_one_advisory_across_projects_and_keeps_two_advisories(self) -> None:
            advisory = "https://github.com/advisories/GHSA-5crp-9r3c-p9vr"
            other = "https://github.com/advisories/GHSA-aaaa-bbbb-cccc"
            findings = parse_nuget_documents(
                [
                    _nuget_document("/runner/src/App/App.csproj", "Newtonsoft.Json", "12.0.3", "High", advisory),
                    _nuget_document(r"C:\repo\src\Lib\Lib.csproj", "Newtonsoft.Json", "12.0.3", "Moderate", advisory),
                    _nuget_document("src/Other/Other.csproj", "Contoso.Widget", "1.0.0", "Critical", other),
                ]
            )
            self.assertEqual(len(findings), 2)
            nuget = next(item for item in findings if "Newtonsoft.Json" in item.title)
            self.assertEqual(nuget.severity, "high")
            self.assertIn("src/App/App.csproj", nuget.details)
            self.assertIn("src/Lib/Lib.csproj", nuget.details)
            self.assertEqual(extract_fingerprint(nuget.body()), nuget.fingerprint)
            self.assertTrue(nuget.fingerprint.startswith(NUGET_PREFIX))
            self.assertIn("ghsa-5crp-9r3c-p9vr", nuget.fingerprint)

        def test_nuget_accepts_camel_case_advisory_and_a_preamble(self) -> None:
            text = 'warning: restored\n{"projects":[{"path":"src/App/App.csproj","frameworks":[{"topLevelPackages":[{"id":"Foo","resolvedVersion":"1.2.3","vulnerabilities":[{"severity":"Low","advisoryUrl":"https://github.com/advisories/GHSA-zzzz"}]}]}]}]}'
            document = load_json_text(text)
            self.assertIsInstance(document, dict)
            findings = parse_nuget_documents([document])  # type: ignore[list-item]
            self.assertEqual(len(findings), 1)
            self.assertEqual(findings[0].severity, "low")

        def test_qodana_files_only_new_smells_and_all_current_security_results(self) -> None:
            baseline = _sarif([_qodana_result("AccessToDisposedClosure", "AAA", message="old")], [SMELL_RULE])
            current = _sarif(
                [
                    _qodana_result("AccessToDisposedClosure", "AAA", state="unchanged"),
                    _qodana_result("AccessToDisposedClosure", "BBB", state="new", message="new smell"),
                    _qodana_result("AccessToDisposedClosure", "DDD", state="absent"),
                    _qodana_result("VulnerableApi", "CCC", state="unchanged", message="vulnerable call", severity="Critical"),
                ],
                [SMELL_RULE, SECURITY_RULE],
            )
            findings, complete = parse_qodana(current, baseline)
            self.assertTrue(complete)
            kinds = sorted((item.kind, item.fingerprint.split(":")[1], item.title) for item in findings)
            self.assertEqual(len(findings), 2)
            smells = [item for item in findings if item.kind == "smell"]
            vulns = [item for item in findings if item.kind == "vulnerability"]
            self.assertEqual(len(smells), 1)
            self.assertIn("bbb", smells[0].fingerprint)
            self.assertIn("src/TotallyHotArcRouter/Proxy/Foo.cs", smells[0].details)
            self.assertEqual(len(vulns), 1)
            self.assertIn("ccc", vulns[0].fingerprint)
            self.assertEqual(vulns[0].severity, "critical")
            self.assertNotIn("AAA", "".join(item.fingerprint for item in findings))
            self.assertTrue(kinds)

        def test_missing_baseline_state_uses_the_baseline_file(self) -> None:
            baseline = _sarif([_qodana_result("AccessToDisposedClosure", "AAA")], [SMELL_RULE])
            current = _sarif(
                [
                    _qodana_result("AccessToDisposedClosure", "AAA"),
                    _qodana_result("AccessToDisposedClosure", "EEE", message="brand new"),
                ],
                [SMELL_RULE],
            )
            findings, _complete = parse_qodana(current, baseline)
            self.assertEqual([item.fingerprint for item in findings], [qodana_fingerprint("smell", "EEE")])

        def test_incomplete_scan_is_flagged(self) -> None:
            sarif = _sarif([_qodana_result("AccessToDisposedClosure", "BBB", state="new")], [SMELL_RULE], successful=False)
            _findings, complete = parse_qodana(sarif, None)
            self.assertFalse(complete)

        def test_plan_creates_updates_skips_and_comments_once(self) -> None:
            finding = Finding(
                fingerprint="vuln:nuget:foo:https://github.com/advisories/ghsa-1",
                kind="vulnerability",
                severity="high",
                title="[vuln] Foo 1.0.0 — ghsa-1",
                details="details",
            )
            created = plan_actions([finding], [], [NUGET_PREFIX], "https://example.test/run/1", True)
            self.assertEqual([item.op for item in created], ["create"])

            open_issue = IssueSnapshot(
                number=4,
                state="open",
                title=finding.title,
                body=finding.body(),
                labels=frozenset(finding.managed_labels()),
            )
            skipped = plan_actions([finding], [open_issue], [NUGET_PREFIX], "https://example.test/run/1", True)
            self.assertEqual([item.op for item in skipped], ["skip"])

            changed = Finding(
                fingerprint=finding.fingerprint,
                kind="vulnerability",
                severity="critical",
                title="[vuln] Foo 1.0.1 — ghsa-1",
                details="details changed",
            )
            updated = plan_actions([changed], [open_issue], [NUGET_PREFIX], "https://example.test/run/1", True)
            self.assertEqual(updated[0].op, "update")
            self.assertIn("severity:critical", updated[0].labels)
            self.assertIn("triage", plan_actions(
                [changed],
                [IssueSnapshot(4, "open", "old", finding.body(), frozenset(finding.managed_labels() | {"triage"}))],
                [NUGET_PREFIX],
                "https://example.test/run/1",
                True,
            )[0].labels)

            closed = IssueSnapshot(8, "closed", finding.title, finding.body(), frozenset(finding.managed_labels()))
            regressed = plan_actions([finding], [closed], [NUGET_PREFIX], "https://example.test/run/1", True)
            self.assertEqual(regressed[0].op, "comment-regressed")
            self.assertNotIn("create", [item.op for item in regressed])
            already = IssueSnapshot(
                8,
                "closed",
                finding.title,
                finding.body(),
                frozenset(finding.managed_labels()),
                comments=(regressed_comment("https://example.test/run/1"),),
            )
            self.assertEqual(
                [item.op for item in plan_actions([finding], [already], [NUGET_PREFIX], "https://example.test/run/2", True)],
                ["skip"],
            )

            gone = plan_actions([], [open_issue], [NUGET_PREFIX], "https://example.test/run/3", True)
            self.assertEqual(gone[0].op, "comment-absent")
            noted = IssueSnapshot(
                4,
                "open",
                finding.title,
                finding.body(),
                frozenset(finding.managed_labels()),
                comments=(absent_comment("https://example.test/run/3"),),
            )
            self.assertEqual(
                [item.op for item in plan_actions([], [noted], [NUGET_PREFIX], "https://example.test/run/4", True)],
                ["skip"],
            )
            self.assertEqual(plan_actions([], [open_issue], [NUGET_PREFIX], "https://example.test/run/3", False), [])

        def test_nuget_scope_does_not_mark_a_qodana_issue_absent(self) -> None:
            qodana_issue = IssueSnapshot(
                3,
                "open",
                "[vuln] VulnerableApi — Foo.cs",
                "<!-- audit-fingerprint: vuln:qodana:abc -->\n\nbody\n",
                frozenset({"audit:vulnerability", "severity:high"}),
            )
            planned = plan_actions([], [qodana_issue], [NUGET_PREFIX], "https://example.test/run/1", True)
            self.assertEqual(planned, [])

        def test_next_link(self) -> None:
            header = '<https://api.github.com/repos/a/b/issues?page=2>; rel="next", <https://api.github.com/repos/a/b/issues?page=3>; rel="last"'
            self.assertEqual(next_link(header), "https://api.github.com/repos/a/b/issues?page=2")
            self.assertIsNone(next_link('<https://api.github.com/x>; rel="last"'))

        def test_cli_dry_run_against_sample_findings(self) -> None:
            advisory = "https://github.com/advisories/GHSA-5crp-9r3c-p9vr"
            script = str(Path(__file__).resolve())
            with tempfile.TemporaryDirectory() as tmp:
                directory = Path(tmp)
                report_dir = directory / "reports"
                report_dir.mkdir()
                report = report_dir / "App.json"
                report.write_text(
                    "the following sources were used\n"
                    + json.dumps(_nuget_document("/work/src/App/App.csproj", "Newtonsoft.Json", "12.0.3", "High", advisory)),
                    encoding="utf-8",
                )
                (directory / "none.json").write_text("[]", encoding="utf-8")
                first = subprocess.run(
                    [
                        sys.executable,
                        script,
                        "sync-nuget",
                        "--input-dir",
                        str(report_dir),
                        "--existing-issues",
                        str(directory / "none.json"),
                        "--dry-run",
                        "--repo",
                        "example/example",
                        "--run-url",
                        "https://example.test/run/1",
                    ],
                    check=False,
                    capture_output=True,
                    text=True,
                )
                self.assertEqual(first.returncode, 0, first.stderr)
                self.assertIn('"op": "create"', first.stdout)
                self.assertIn("created=1", first.stdout)
                created = json.loads(next(line for line in first.stdout.splitlines() if '"op": "create"' in line))
                existing = directory / "existing.json"
                existing.write_text(
                    json.dumps(
                        [
                            {
                                "number": 15,
                                "state": "open",
                                "title": created["title"],
                                "body": parse_nuget_directory(report_dir)[0].body(),
                                "labels": created["labels"],
                                "comments": [],
                            }
                        ]
                    ),
                    encoding="utf-8",
                )
                second = subprocess.run(
                    [
                        sys.executable,
                        script,
                        "sync-nuget",
                        "--input-dir",
                        str(report_dir),
                        "--existing-issues",
                        str(existing),
                        "--dry-run",
                        "--repo",
                        "example/example",
                        "--run-url",
                        "https://example.test/run/2",
                    ],
                    check=False,
                    capture_output=True,
                    text=True,
                )
                self.assertEqual(second.returncode, 0, second.stderr)
                self.assertIn("skipped=1", second.stdout)
                self.assertNotIn('"op": "create"', second.stdout)

        def test_workflow_cannot_push_or_open_pull_requests(self) -> None:
            workflow = Path(__file__).resolve().parents[1] / "workflows" / "nightly-audit.yml"
            text = workflow.read_text(encoding="utf-8")
            for required in (
                "0 10 * * *",
                "workflow_dispatch:",
                "issues: write",
                "contents: read",
                "push-fixes: none",
                "post-pr-comment: false",
                "pr-mode: false",
                "persist-credentials: false",
                "--baseline .qodana/qodana.sarif.json",
                "TotallyHotArcRouter.Tray",
            ):
                self.assertIn(required, text)
            for forbidden in (
                "pull_request:",
                "contents: write",
                "pull-requests:",
                "security-events:",
                "push-fixes: pull-request",
                "push-fixes: branch",
            ):
                self.assertNotIn(forbidden, text)
            shell = Path(__file__).resolve().parent / "collect-vulnerable-packages.sh"
            self.assertIn("dotnet list", shell.read_text(encoding="utf-8"))

    suite = unittest.defaultTestLoader.loadTestsFromTestCase(NightlyAuditTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
        sys.exit(_run_self_test())
    sys.exit(main())
