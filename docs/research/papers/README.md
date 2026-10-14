# Paper index

Sixteen papers cited by [the evaluator shortlist](../fast-trainable-evaluators.md). Each file is either a full conversion of the arXiv HTML or an original summary. Nothing in these files was written to fill a gap in the paper: a number appears only when the HTML text that was read states it.

Retrieved 2026-10-14. Every abstract page (`https://arxiv.org/abs/<id>`) and every HTML version (`https://arxiv.org/html/<id><version>`) returned HTTP 200. No link failed.

Full conversions are adaptations of HTML that the authors licensed under [CC BY 4.0](http://creativecommons.org/licenses/by/4.0/). Those files stay under CC BY 4.0. They are not covered by this repository's AGPL. Summaries exist where the license does not allow a conversion to be redistributed here.

| Group | arXiv | Paper | Year | License checked on the abstract page | Record | File |
|---|---|---|---|---|---|---|
| Routing | [2505.12601](https://arxiv.org/abs/2505.12601) | Li, Rethinking Predictive Modeling for LLM Routing | 2025 (v2, 2026) | CC BY 4.0 | Full conversion of v2 HTML | [2505.12601-knn-beats-learned-routers.md](2505.12601-knn-beats-learned-routers.md) |
| Routing | [2406.18665](https://arxiv.org/abs/2406.18665) | Ong et al., RouteLLM | 2024 (v4, 2025) | arXiv non-exclusive 1.0 | Summary of v4 HTML | [2406.18665-routellm.md](2406.18665-routellm.md) |
| Routing | [2502.18482](https://arxiv.org/abs/2502.18482) | Wang et al., MixLLM | 2025 | CC BY 4.0 | Full conversion of v1 HTML | [2502.18482-mixllm.md](2502.18482-mixllm.md) |
| Routing | [2506.01048](https://arxiv.org/abs/2506.01048) | Song et al., IRT-Router | 2025 | arXiv non-exclusive 1.0 | Summary of v2 HTML | [2506.01048-irt-router.md](2506.01048-irt-router.md) |
| Routing | [2404.14618](https://arxiv.org/abs/2404.14618) | Ding et al., Hybrid LLM | 2024 | CC BY-NC-ND 4.0 | Summary of v1 HTML, abstract not copied | [2404.14618-hybrid-llm.md](2404.14618-hybrid-llm.md) |
| Routing | [2409.19886](https://arxiv.org/abs/2409.19886) | Chen et al., RouterDC | 2024 | arXiv non-exclusive 1.0 | Summary of v1 HTML | [2409.19886-routerdc.md](2409.19886-routerdc.md) |
| Routing | [2506.16655](https://arxiv.org/abs/2506.16655) | Tran et al., Arch-Router | 2025 | CC BY 4.0 | Full conversion of v1 HTML | [2506.16655-arch-router.md](2506.16655-arch-router.md) |
| Routing | [2305.05176](https://arxiv.org/abs/2305.05176) | Chen, Zaharia, and Zou, FrugalGPT | 2023 | arXiv non-exclusive 1.0 | Summary of v1 HTML | [2305.05176-frugalgpt.md](2305.05176-frugalgpt.md) |
| Routing | [2310.12963](https://arxiv.org/abs/2310.12963) | Aggarwal, Madaan, et al., AutoMix | 2023 (v5, 2025) | CC BY 4.0 | Full conversion of v5 HTML | [2310.12963-automix.md](2310.12963-automix.md) |
| Routing | [2410.03834](https://arxiv.org/abs/2410.03834) | Feng, Shen, and You, GraphRouter | 2024 (v2, 2025) | CC BY 4.0 | Full conversion of v2 HTML | [2410.03834-graphrouter.md](2410.03834-graphrouter.md) |
| Calibration | [1706.04599](https://arxiv.org/abs/1706.04599) | Guo et al., On Calibration of Modern Neural Networks | 2017 | arXiv non-exclusive 1.0 | Summary of v2 HTML | [1706.04599-temperature-scaling.md](1706.04599-temperature-scaling.md) |
| Calibration | [2107.07511](https://arxiv.org/abs/2107.07511) | Angelopoulos and Bates, conformal prediction | 2021 (v6, 2022) | arXiv non-exclusive 1.0 | Summary of v6 HTML | [2107.07511-conformal-prediction.md](2107.07511-conformal-prediction.md) |
| Embeddings | [2209.11055](https://arxiv.org/abs/2209.11055) | Tunstall et al., Efficient Few-Shot Learning Without Prompts (SetFit) | 2022 | CC BY 4.0 | Full conversion of v1 HTML | [2209.11055-setfit.md](2209.11055-setfit.md) |
| Embeddings | [2410.02223](https://arxiv.org/abs/2410.02223) | Zhuang et al., EmbedLLM | 2024 | CC BY 4.0 | Full conversion of v2 HTML | [2410.02223-embedllm.md](2410.02223-embedllm.md) |
| Embeddings | [2412.13663](https://arxiv.org/abs/2412.13663) | Warner et al., ModernBERT | 2024 | CC BY 4.0 | Full conversion of v2 HTML | [2412.13663-modernbert.md](2412.13663-modernbert.md) |
| Benchmarks | [2403.12031](https://arxiv.org/abs/2403.12031) | Hu et al., RouterBench | 2024 | arXiv non-exclusive 1.0 | Summary of v2 HTML | [2403.12031-routerbench.md](2403.12031-routerbench.md) |

## Why some papers are summaries

- **arXiv non-exclusive 1.0** lets arXiv distribute the work. It does not let this repository republish the paper. Those seven files quote the abstract and summarize the method and results from the HTML. arXiv's [license page](https://info.arxiv.org/help/license/index.html) states that CC0 1.0 applies to all metadata. The quoted text is that abstract metadata. The paper body is not republished.
- **CC BY-NC-ND 4.0** (Hybrid LLM only) forbids derivatives and limits sharing to non-commercial use. This repository is AGPL and may be used commercially, so the file does not copy the abstract and does not convert the paper.
- **CC BY 4.0** allows an adaptation with attribution. Those eight files are the HTML converted to Markdown. Math comes from the HTML TeX annotations. Figures are linked, not embedded. LaTeXML equation rows remain as one-row tables.

The shortlist was written from abstracts on 2026-10-13 and says the full PDFs were not read. The files in this folder were read from the arXiv HTML on 2026-10-14. Where the two disagree, the paper file is the closer reading. The plan records the disagreements that matter for pricing.
