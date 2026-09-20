<div align="center">

# TotallyHot Arc Router

**Purpose-built model routing for coding workloads.**

</div>

TotallyHot Arc Router routes coding tasks to different backend models under a
performance-cost tradeoff.

## Point a client

One OpenAI-compatible base URL. Send `"model": "auto"` and the router picks the model.

**Base URL**

```
https://localhost:47101/v1
```

**Copy-paste**

```bash
export OPENAI_BASE_URL=https://localhost:47101/v1
export OPENAI_API_KEY=not-needed
```

```python
from openai import OpenAI

client = OpenAI(base_url="https://localhost:47101/v1", api_key="not-needed")
client.chat.completions.create(
    model="auto",
    messages=[{"role": "user", "content": "hello"}],
)
```

```bash
curl https://localhost:47101/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"auto","messages":[{"role":"user","content":"hello"}]}'
```

Dashboard: `https://localhost:47104`. Windows/Linux/macOS installers already trust the local CA. Docker and browsers that ignore the OS store: [client TLS setup](docs/router/client-tls-setup.md). The `OPENAI_API_KEY` value is a placeholder — LLM forwarding is not authenticated; it only satisfies clients that refuse an empty key.

## Install

TotallyHot Arc Router is cross-platform (web GUI migration plan Phases P6-P10): Windows, Linux, and
macOS all run the same router, with a browser-based dashboard instead of a platform-specific GUI.

**Windows** — Windows 10 version 1809 (build 17763) or later, x64. Download the `.msi` from the
[latest release](https://github.com/davidpizon/TotallyHot-ArcRouter/releases/latest)
and run it. Nothing else is required - both the router and the small system-tray companion ship as
self-contained builds, so no .NET runtime needs to be installed first.

The installer is per-machine and needs administrator rights. It lays out
`%ProgramFiles%\TotallyHotArcRouter\Router\` and `\Tray\`, then registers and
starts `TotallyHotArcRouter`, a `LocalSystem` Windows Service set to start
automatically - so the router is running before you open the dashboard, and keeps
running after you close it. The tray icon opens the dashboard in your default
browser, toggles routing, and shows service status.

**Windows will warn you on first run.** The MSI is not code-signed yet, so
SmartScreen shows "Windows protected your PC" and names an unknown publisher;
choose *More info* → *Run anyway*. Every release publishes a `checksums.txt`
asset next to every installer/archive asset if you would rather verify the
download before trusting it - the hash on the left should match:

```powershell
Get-FileHash .\TotallyHotArcRouter-1.0.0.msi -Algorithm SHA256
```

**Linux / macOS** — download the `totallyhotarcrouter-<rid>.tar.gz` for your platform
(`linux-x64`, `linux-arm64`, or `osx-arm64`) from the same releases page, extract it, and run the
matching install script as root/sudo:

```bash
tar xzf totallyhotarcrouter-linux-x64.tar.gz
cd totallyhotarcrouter-linux-x64
sudo ./packaging/linux/install.sh   # or ./packaging/macos/install.sh on macOS
```

This registers a systemd service (Linux) or LaunchDaemon (macOS) running as a dedicated unprivileged
account, and trusts the router's local CA. See [`packaging/linux/install.sh`](packaging/linux/install.sh)
and [`packaging/macos/install.sh`](packaging/macos/install.sh) for what each step does, and
[`docs/router/client-tls-setup.md`](docs/router/client-tls-setup.md) for trusting the CA in a browser
that doesn't read the OS trust store (Firefox, Chrome-on-Linux).

**Docker** — a multi-arch image is published to
`ghcr.io/davidpizon/totallyhot-arcrouter:latest`; see [`src/README.md`](src/README.md#running-with-docker)
or the [`Dockerfile`](src/TotallyHotArcRouter/Dockerfile)'s own header comment for the run command and
first-time trust setup.

Once running, open `https://localhost:47104` for the dashboard and point a client using
[Point a client](#point-a-client) (see
[`docs/router/client-tls-setup.md`](docs/router/client-tls-setup.md) if a client or browser does not
yet trust the local CA).

Releases marked **Pre-release** on the releases page are release candidates. They are
built by the same pipeline and are safe to install, but they are deliberately
invisible to the built-in update check, which only ever offers a promoted
release (see
[`docs/router/packaging-and-distribution.md`](docs/router/packaging-and-distribution.md)
§7). Once installed, the router checks for new releases every six hours. On Windows the tray offers to
apply what it finds via the MSI; on Linux/macOS an update is detected but must currently be applied by
re-running the install script with a newer archive (no in-process apply path yet - see the web GUI
migration plan's P10 section). Updates are never applied without an explicit action.

## Routing

The drop-in above is the whole client setup: one base URL, `"model": "auto"`
(any casing). A request naming a real, servable model (e.g. `"model": "gpt-5.4"`)
is always served exactly that model; the router never substitutes a model the
client explicitly named. An unrecognized name, an administratively stopped
model, or a circuit-open/unhealthy provider fall back to the same routing
decision as `auto`.

How that decision is scored against the policy that never learned — estimated
regret, score-delta, and the Cost Analytics report card — is documented in
[`docs/score-delta-methodology.md`](docs/score-delta-methodology.md). The
plain-language loop that produces those receipts is
[`docs/how-it-learns.md`](docs/how-it-learns.md).

Every response — streaming or buffered — carries three headers reporting what
happened:

| Header | Meaning |
|---|---|
| `X-ArcRouter-Requested-Model` | The client's literal `model` string. |
| `X-ArcRouter-Routed-Model` | The model that actually served the request (post-failover, post-substitution). |
| `X-ArcRouter-Substitution-Reason` | Why they differ: `None`, `AutoSelect`, `UnresolvedName`, `ModelStopped`, `CircuitOpen`, or `Failover`. |

## Data

This project **evaluates against** CodeRouterBench, a task-by-model benchmark
published by Zhou et al. alongside
[arXiv:2606.22902](https://arxiv.org/abs/2606.22902). The dataset is their work,
not this repository's: it is published on
[Hugging Face](https://huggingface.co/datasets/Lance1573/CodeRouterBench) under
the MIT license, and this repository consumes it as an external dependency.

The tables are synced on demand into a local SQLite database
(`coderouterbench.db`) rather than checked in - via Governance → Benchmark
Data in the GUI, the `sync_benchmark_data` MCP tool, or
`TotallyHotArcRouter --sync-benchmark-data`
(see [`data/README.md`](data/README.md) and
[`docs/router/coderouterbench-sqlite-migration-plan.md`](docs/router/coderouterbench-sqlite-migration-plan.md)
for what it syncs and verifies):

- `benchmark_id_results` (`split='probing'` union `split='id_test'`): 9,999 ID tasks x 8 models.
- `benchmark_ood_results`: 176 OOD tasks x 8 models.
- `benchmark_id_tasks` and `benchmark_ood_tasks`: task metadata.
- `benchmark_models`: canonical backend models and USD pricing.

`outputs/` and `agentic-artifacts/` are not restored yet - see
[`data/README.md`](data/README.md)'s "Not yet restored" section.

## Project Layout

```text
src/TotallyHotArcRouter*/             .NET router, web dashboard (Blazor WebAssembly), Windows tray, quality verifier, tests
packaging/linux/, packaging/macos/    systemd/LaunchDaemon units and install scripts (cross-platform service packaging)
docs/                                 Design docs and handbook

%ProgramData%\TotallyHotArcRouter\coderouterbench.db   CodeRouterBench tables, synced on demand (Linux/macOS: see AppDataPaths)
```

## Architecture and code quality

Architecture decisions are recorded under [`docs/adr/`](docs/adr/README.md) (template, numbering,
and the index of accepted vs proposed records).

Code-quality work uses a **dual-engine pipeline**: CodeGraph MCP maps structure (call paths, type
hierarchies, blast radius) and Serena MCP classifies smells and proposes phased refactors. The
standing method, severity matrix, and a dated production-`src/` catalog are
[ADR-0008](docs/adr/0008-codegraph-serena-dual-engine-code-smell-pipeline.md). Mechanical extracts
and the validation gate (zero-warning build, tests, coverage floor, hot-path smoke) stay in
[`docs/router/code-smell-refactoring-plan.md`](docs/router/code-smell-refactoring-plan.md) — ADR-0008
complements that plan rather than replacing it. Agent instructions for keeping the two engines
paired are in [`AGENTS.md`](AGENTS.md).

## License

TotallyHot Arc Router is licensed under the
[GNU Affero General Public License v3.0](LICENSE), with an
[additional permission](LICENSE.exceptions.md) for linking against Microsoft
platform components. As of the web GUI migration plan (2026-09-15), no shipped
component actually requires this permission - the WebView2-dependent Windows
GUI it was written for is retired in favor of a cross-platform Blazor
WebAssembly dashboard - but the exception is kept in force for any future
Windows-specific component that might need it; see
[`LICENSE.exceptions.md`](LICENSE.exceptions.md) for the current status.

In short: you may use, modify, and redistribute this software freely, but if you
distribute it — or run a modified version as a network service — you must make
your corresponding source available under the same terms.

Third-party components are attributed in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md). Every dependency this
project redistributes is MIT, Apache-2.0, or BSD-3-Clause. The Microsoft
platform components above are prerequisites obtained from Microsoft under
Microsoft's own terms, not redistributed here. Either way, no dependency
imposes copyleft on this project.

Copyright © 2026 David Pizon.

## Citation

This project builds on two pieces of published research.

**The routing approach and the CodeRouterBench dataset** this project evaluates
against are the work of Zhou et al. If you use them, cite their paper:

```bibtex
@article{agent2026zhou,
  title         = {Agent-as-a-Router: Agentic Model Routing for Coding Tasks},
  author        = {Pengfei Zhou, Zhiwei Tang, Yixing Ma, Jiasheng Tang, Yizeng Han, Zhenglin Wan, Fanqing Meng, Wei Wang, Bohan Zhuang, Wangbo Zhao, Yang You},
  journal       = {arXiv preprint arXiv:2606.22902},
  year          = {2026},
  archivePrefix = {arXiv},
  eprint        = {2606.22902},
  url           = {https://arxiv.org/abs/2606.22902}
}
```

**The quality-scoring judge** is an implementation of G-Eval, by Liu et al. Arc
Router grades every response with two independent graders, and one of them
(`GEvalJudgeClient`) follows this recipe directly: a per-dimension criteria
prompt, a 1–5 form-filling score, and — the part that matters most — a
**probability-weighted** score taken over the output-token logprobs rather than
the single digit the model happened to sample. If you use that grader, cite:

```bibtex
@inproceedings{geval2023liu,
  title         = {G-Eval: NLG Evaluation Using GPT-4 with Better Human Alignment},
  author        = {Yang Liu, Dan Iter, Yichong Xu, Shuohang Wang, Ruochen Xu, Chenguang Zhu},
  booktitle     = {Proceedings of the 2023 Conference on Empirical Methods in Natural Language Processing (EMNLP)},
  year          = {2023},
  archivePrefix = {arXiv},
  eprint        = {2303.16634},
  url           = {https://arxiv.org/abs/2303.16634}
}
```

The paper's own headline caveat applies here and is worth repeating: G-Eval
**prefers LLM-generated text over human-written text**, even where human judges
prefer the human text. Arc Router uses judge scores to *choose between models*,
never as a training reward, which avoids the self-reinforcement failure the
authors warn about — but the related risk of the judge systematically favouring
one model family is real and is not yet measured. See
[`docs/router/geval-shadow-scoring-plan.md`](docs/router/geval-shadow-scoring-plan.md)
§G2 for the self-preference probe that is meant to quantify it.

Agent-readable digests of both papers live in
[`docs/research/`](docs/research/), alongside the original PDFs.

