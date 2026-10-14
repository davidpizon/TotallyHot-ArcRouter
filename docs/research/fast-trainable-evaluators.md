> Saved 2026-10-14 from the 2026-10-13 research write-up. The text below is that write-up. Paper files, licenses, and which texts are full conversions are indexed in [papers/README.md](papers/README.md). The write-up was prepared from abstracts; the paper files were added later from the arXiv HTML.

# Arc Router: fast, trainable, self-hostable context evaluators (research shortlist)
*Prepared 2026-10-13 PT. Read-only. Every arXiv link below was opened (HTTP 200) and its title/authors checked on 2026-10-13. Numbers are ONLY those stated in the arXiv abstracts; anything else is marked [inference] or [not verified].*
Mapping: (a) predict cost/quality per model, (b) categorize request, (c) end-to-end decision from text + prices.

## Ranked shortlist
1. **kNN routing**: Li, "Rethinking Predictive Modeling for LLM Routing: When Simple kNN Beats Complex Learned Routers", arXiv:2505.12601, https://arxiv.org/abs/2505.12601
   - Method: a tuned kNN over query embeddings matches or often beats learned routers. Abstract credits the locality of model performance in embedding space. Also releases standardized routing benchmarks, including a multimodal one.
   - Latency/size: no network beyond the embedder [inference].
   - Data: labeled (query, model, score) history. Abstract claims lower sample complexity.
   - Calibration: neighbor vote fractions are only roughly calibrated [inference]. Add temperature scaling or Platt scaling (#8).
   - Maps to: (a), and to (c) via argmax of predicted score minus λ·price.
   - Fit: **Excellent.** Arc already has kNN memory over BGE. Use this paper to tune k, the 0.5 cosine threshold and the weighting, and as the baseline any new voter must beat.
2. **RouteLLM**: Ong et al., arXiv:2406.18665, https://arxiv.org/abs/2406.18665
   - Method: strong-vs-weak routers trained on human preference data plus augmentation. The variants are matrix factorization, a BERT classifier, a causal LLM and similarity-weighted ranking [variant list from the paper/repo, not the abstract].
   - Reported: "reduces costs by over 2 times in certain cases" without losing quality, and the routers transfer to other model pairs.
   - Data: preference pairs (Chatbot Arena style) plus augmentation.
   - Calibration: outputs P(strong model wins). Pick the threshold for a cost target [inference].
   - Maps to: (a), and (c) for two tiers.
   - Fit: **High.** MF and BERT heads are small and ONNX-exportable. MF can sit on top of BGE embeddings [inference]. Natural replacement or companion for the logreg voter. Extending it to N models needs per-model heads.
3. **MixLLM**: Wang et al., arXiv:2502.18482, https://arxiv.org/abs/2502.18482
   - Method: contextual-bandit routing. Query tags enrich the embeddings. Lightweight predictors estimate quality AND cost for each LLM, and a meta-decision maker balances quality, cost and latency. Supports continual learning and adding or removing LLMs.
   - Latency/size: "lightweight" per the abstract. Numbers [not verified].
   - Data: online feedback.
   - Calibration: through the bandit's uncertainty [inference].
   - Maps to: **(a)+(b)+(c)**. This is the closest match to #291 (cost-aware reward) plus #292 (periodic retraining).
   - Fit: **High** as a design blueprint. The predictors can be small heads on BGE embeddings [inference].
4. **IRT-Router**: Song et al., arXiv:2506.01048, https://arxiv.org/abs/2506.01048
   - Method: Item Response Theory relates query attributes (difficulty) to LLM ability and predicts response performance, plus an online warm-up based on semantic similarity.
   - Calibration: the IRT logistic link gives P(correct). These are probabilities by construction, but still need an empirical check [inference].
   - Maps to: (a), and (c) with a cost term (the paper balances performance and cost).
   - Fit: **High.** It is a small parametric model and interpretable (ability and difficulty), and the warm-up resembles Arc's kNN memory.
5. **Hybrid LLM**: Ding et al., arXiv:2404.14618, https://arxiv.org/abs/2404.14618
   - Method: a router predicts query difficulty (the expected quality gap between small and large model), with a quality threshold tunable at test time.
   - Reported: "up to 40% fewer calls to the large model, with no drop in response quality".
   - Data: quality scores of both models' responses per query.
   - Maps to: (a), and to (c) for two tiers.
   - Fit: **High.** Small encoder classifier [inference]. The tunable threshold suits a cost knob.
6. **RouterDC**: Chen et al., arXiv:2409.19886, https://arxiv.org/abs/2409.19886
   - Method: a query encoder plus learned LLM embeddings, trained with two contrastive losses. Handles queries where multiple LLMs do well.
   - Reported: +2.76% in-distribution and +1.90% out-of-distribution over baselines.
   - Calibration: softmax similarity, uncalibrated [inference].
   - Maps to: (a)/(c). Cost is not in the abstract.
   - Fit: **Medium-high.** It is encoder-based and ONNX-exportable, and could fine-tune or replace the BGE stage.
7. **EmbedLLM**: Zhuang et al., arXiv:2410.02223, https://arxiv.org/abs/2410.02223
   - Method: an encoder-decoder learns compact model embeddings. Claims better routing accuracy and latency than prior methods, and forecasts benchmark performance.
   - Maps to: (a).
   - Fit: **Medium.** Useful for adding agents cheaply (one model vector each).
8. **Calibration layer**:
   - Guo et al., "On Calibration of Modern Neural Networks", arXiv:1706.04599, https://arxiv.org/abs/1706.04599. Temperature scaling, a one-parameter Platt variant, is "surprisingly effective".
   - Angelopoulos & Bates, conformal prediction intro, arXiv:2107.07511, https://arxiv.org/abs/2107.07511. Gives distribution-free prediction sets with coverage guarantees and works with any pretrained model.
   - Maps to: calibrates (a), (b) and (c). Conformal sets give a principled "abstain / escalate if the set has more than one model" rule [inference]. That matches Arc's abstaining voters.
   - Fit: **Excellent.** A few parameters, fitted on a held-out split during #292's epoch.
9. **SetFit**: Tunstall et al., arXiv:2209.11055, https://arxiv.org/abs/2209.11055
   - Method: contrastive fine-tuning of a sentence transformer on a few labeled pairs, then a classification head. Prompt-free, with orders of magnitude fewer parameters than LLM few-shot methods.
   - Maps to: **(b)** category, and possibly (a).
   - Fit: **High.** Can fine-tune BGE itself and export it to ONNX [inference].
10. **ModernBERT**: Warner et al., arXiv:2412.13663, https://arxiv.org/abs/2412.13663
    - Method: an encoder with native 8192-token context, claimed most speed- and memory-efficient encoder (designed for common GPUs).
    - Use: backbone for a fine-tuned cross-encoder or classifier for (a)/(b).
    - Fit: **Medium.** Better than BGE-large on long prompts, but a second model to run [inference].
11. **Arch-Router**: Tran et al., arXiv:2506.16655, https://arxiv.org/abs/2506.16655
    - Method: a compact **1.5B** model maps queries to user-defined domain/action policies. New models can be added without retraining.
    - Maps to: (b), then a policy-based (c).
    - Fit: **Low-medium for the hot path.** 1.5B is heavy for CPU per request [inference]. Best used as a teacher or labeler for a small classifier.
12. **Cascades (reference)**:
    - FrugalGPT, Chen et al., arXiv:2305.05176, https://arxiv.org/abs/2305.05176. A learned LLM cascade that reduces cost while matching the best single LLM.
    - AutoMix, Aggarwal et al., arXiv:2310.12963, https://arxiv.org/abs/2310.12963. Few-shot self-verification plus a POMDP router.
    - Both need a first model call before deciding. Not pre-routing evaluators, but useful as fallback or escalation logic.
13. **GraphRouter**: Feng et al., arXiv:2410.03834, https://arxiv.org/abs/2410.03834
    - Method: a heterogeneous graph (task, query and LLM nodes) with edge prediction, inductive to new LLMs.
    - Fit: **Low-medium.** A GNN is harder to run in C#/ONNX [inference].
- **Benchmark: RouterBench**, Hu et al., arXiv:2403.12031, https://arxiv.org/abs/2403.12031. Over 405k inference outcomes for training and evaluating routers. Use it to pretrain the (a) predictors and as offline evaluation for #291 and #292.

## Recommended stack [inference]
BGE embedding (or a SetFit-tuned BGE) feeds per-model quality heads (logreg, MF, IRT) plus the existing kNN. Temperature-scale or conformal-calibrate on a holdout. Decide by argmax E[quality] − λ·price, which is #291. Abstain or escalate when the conformal set is ambiguous. Refit heads and calibration each #292 epoch, and benchmark on RouterBench.

## Jev
Hosted only. Claims: a parallel sampler, RLCD calibration, and typed choice/score/yes-no outputs (see /workspace/jev_briefing.md). Unverified, and no paper. The closest self-hostable equivalent is a frozen encoder with small typed heads: softmax over K options (choice), an ordinal or regression head (score), and a sigmoid (yes/no), each temperature-scaled. Items 1–4, 8 and 9 above give exactly that in one forward pass [inference]. For open-ended "typed questions" without training, a small local LLM with constrained decoding over the label tokens is the fallback.

## Gaps
- I verified no conformal-prediction paper specific to LLM routers. Only the general method is cited.
- Latency, parameter counts and calibration (ECE) figures were not in the abstracts. I did not read the full PDFs, so they are unverified.
- No paper here directly takes raw prices as an input feature for an end-to-end (c). Most use a fixed λ trade-off.
- I did not check whether each paper's code exports to ONNX.
