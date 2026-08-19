<div align="center">

# TotallyHot Arc Router

**Purpose-built model routing for coding workloads.**

</div>

TotallyHot Arc Router routes coding tasks to different backend models under a
performance-cost tradeoff.

## Routing

Point an OpenAI/Anthropic-compatible client at the proxy and send `"model": "auto"`
(any casing) to opt a request into routing — the router picks the model. A
request naming a real, servable model (e.g. `"model": "gpt-5.4"`) is always
served exactly that model; the router never substitutes a model the client
explicitly named. An unrecognized name, an administratively stopped model, or
a circuit-open/unhealthy provider fall back to the same routing decision as
`auto`.

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
src/TotallyHotArcRouter*/             .NET router implementation, GUI, sandbox, tests
docs/                           Design docs and handbook

%LOCALAPPDATA%\TotallyHot.ArcRouter\coderouterbench.db   CodeRouterBench tables, synced on demand
```

## License

TotallyHot Arc Router is licensed under the
[GNU Affero General Public License v3.0](LICENSE), with an
[additional permission](LICENSE.exceptions.md) for linking against Microsoft
platform components (WebView2, Windows App SDK).

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

The routing approach and the CodeRouterBench dataset this project evaluates
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

