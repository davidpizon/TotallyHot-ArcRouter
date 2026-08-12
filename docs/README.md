<div align="center">

# TotallyHot Arc Router

**Purpose-built model routing for coding workloads.**
</div>

TotallyHot Arc Router routes coding tasks to different backend models under a
performance-cost tradeoff. This repository contains the .NET (C#)
implementation of the router plus a fetch script for the CodeRouterBench data
used by this project.

For the full CodeRouterBench dataset and paper, see the
[Hugging Face dataset](https://huggingface.co/datasets/Lance1573/CodeRouterBench)
and [arXiv:2606.22902](https://arxiv.org/abs/2606.22902).

## Data

CodeRouterBench is a task-by-model benchmark release. The Python reproduction
pipeline that generated it is no longer part of this repository, and the
tables themselves are fetched on demand rather than checked in - run
`scripts/fetch-coderouterbench.sh` to restore them into `data/coderouterbench/`
(see [`../data/README.md`](../data/README.md) for what it fetches and verifies):

- `data/coderouterbench/id_results_long.csv`: 9,999 ID tasks x 8 models.
- `data/coderouterbench/ood176_results_long.csv`: 176 OOD tasks x 8 models.
- `data/coderouterbench/id_tasks.jsonl` and `ood176_tasks.jsonl`: task metadata.
- `data/coderouterbench/models.json`: canonical backend models and USD pricing.

`outputs/` and `agentic-artifacts/` are not restored yet - see
[`../data/README.md`](../data/README.md)'s "Not yet restored" section.

## Project Layout

```text
src/TotallyHotArcRouter*/             .NET router implementation, GUI, sandbox, tests
docs/                           Design docs and handbook
scripts/                        Data-fetch scripts (not a build/test pipeline)

data/coderouterbench/           CodeRouterBench tables, fetched on demand (gitignored)
```

## Citation

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

