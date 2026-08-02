<div align="center">

# TotallyHot Arc Router

**Purpose-built model routing for coding workloads.**

</div>

TotallyHot Arc Router routes coding tasks to different backend models under a
performance-cost tradeoff.

## Data

CodeRouterBench is a task-by-model benchmark release, kept here for reference
and audit even though the Python reproduction pipeline that generated it is no
longer part of this repository:

- `data/coderouterbench/id_results_long.csv`: 9,999 ID tasks x 8 models.
- `data/coderouterbench/ood176_results_long.csv`: 176 OOD tasks x 8 models.
- `data/coderouterbench/id_tasks.jsonl` and `ood176_tasks.jsonl`: task metadata.
- `data/coderouterbench/models.json`: canonical backend models and USD pricing.
- `outputs/baselines_ood176/`: checked-in reference tables and decisions.

The public OOD176 matrix is under
`data/matrices/phase2_ood/unified/matrix_acrouter_ood176.json`. Raw old112 and
new64 snapshots remain under `data/matrices/phase2_ood/raw/`.

## Project Layout

```text
src/TotallyHotArcRouter*/             .NET router implementation, GUI, sandbox, tests
docs/                           Design docs and handbook

data/coderouterbench/           Public CodeRouterBench tables
data/matrices/                  ID/OOD source matrices and pricing
outputs/                        Checked-in reference outputs
agentic-artifacts/              Agent-readable research evidence
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

