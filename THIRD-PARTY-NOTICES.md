# Third-Party Notices

TotallyHot Arc Router incorporates and depends on third-party software. This file is provided to
satisfy the attribution and notice-retention requirements of those components' licenses.

TotallyHot Arc Router itself is licensed under the GNU Affero General Public License v3.0
(see [`LICENSE`](LICENSE)) with an additional permission for Microsoft platform components
(see [`LICENSE.exceptions.md`](LICENSE.exceptions.md)). Nothing in this file alters the license of
this project; each component below remains under its own license.

**License summary of everything this project redistributes** (§1 - the components that ship inside the
router service, the MAUI GUI, or the published installer): MIT, Apache-2.0, and BSD-3-Clause only.

Two categories fall outside that summary because this project does not redistribute them:

- **Microsoft platform prerequisites** (§2 - Edge WebView2 Runtime, Windows App SDK) are governed by
  Microsoft's own license terms. End users obtain them from Microsoft; the AGPL §7 additional
  permission in [`LICENSE.exceptions.md`](LICENSE.exceptions.md) covers linking against them.
- **One build- and test-time only package**
  (`Microsoft.VisualStudio.Azure.Containers.Tools.Targets`, §3) is under the Microsoft Software
  License Terms. It supplies MSBuild targets, ships in no distributed artifact, and is not
  redistributable as a standalone offering.

Data and models fetched at runtime (§4) are likewise not redistributed and are listed with their own
licenses there.

Across every category, no copyleft (GPL/LGPL/AGPL/MPL/EPL) dependency is used, so no third-party
license compels the licensing choice made for this project.

---

## 1. Components redistributed in binary or source form

These ship inside the router service, the MAUI GUI, or the published installer.

### Apache License 2.0

The following are licensed under the Apache License, Version 2.0. You may obtain a copy of the
License at <https://www.apache.org/licenses/LICENSE-2.0>. Unless required by applicable law or
agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.

| Component | Version | Copyright |
|---|---|---|
| Apache ECharts (`wwwroot/lib/echarts/echarts.min.js`) | vendored | Copyright 2017–2024 The Apache Software Foundation |
| Serilog | 4.4.0 | Copyright Serilog Contributors |
| Serilog.Extensions.Hosting | 10.0.0 | Copyright Serilog Contributors |
| Serilog.Settings.Configuration | 10.0.1 | Copyright Serilog Contributors |
| Serilog.Sinks.Console | 6.1.1 | Copyright Serilog Contributors |
| Grpc.AspNetCore | 2.83.0 | Copyright The gRPC Authors |
| Grpc.Net.Client | 2.83.0 | Copyright The gRPC Authors |
| AWSSDK.BedrockRuntime | 4.0.101.1 | Copyright Amazon.com, Inc. or its affiliates |
| ModelContextProtocol | 2.1.0 | Copyright the ModelContextProtocol C# SDK authors |
| ModelContextProtocol.AspNetCore | 2.1.0 | Copyright the ModelContextProtocol C# SDK authors |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Copyright Eric Sink and contributors |

**Apache ECharts NOTICE** — reproduced as required by Apache-2.0 §4(d). The upstream `LICENSE` and
`NOTICE` files are retained verbatim alongside the vendored bundle in
`src/TotallyHotArcRouter.Gui/wwwroot/lib/echarts/`:

```
Apache ECharts
Copyright 2017-2024 The Apache Software Foundation

This product includes software developed at
The Apache Software Foundation (https://www.apache.org/).
```

> Note on SQLite itself: the native `e_sqlite3` library bundled by SQLitePCLRaw is the SQLite
> database engine, which its authors have released into the **public domain**. The SQLitePCLRaw
> wrapper is Apache-2.0 as listed above.

### MIT License

The following are licensed under the MIT License. Permission is hereby granted, free of charge, to
any person obtaining a copy of this software and associated documentation files to deal in the
Software without restriction, subject to the condition that the above copyright notice and this
permission notice be included in all copies or substantial portions of the Software. THE SOFTWARE IS
PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.

| Component | Version | Copyright |
|---|---|---|
| .NET Runtime, ASP.NET Core, and BCL | 10.0 | Copyright (c) .NET Foundation and Contributors |
| Microsoft.Maui.Controls | 10.0.90 | Copyright (c) Microsoft Corporation |
| Microsoft.AspNetCore.Components.WebView.Maui | 10.0.90 | Copyright (c) Microsoft Corporation |
| Microsoft.CodeAnalysis.CSharp (Roslyn) | 5.6.0 | Copyright (c) Microsoft Corporation |
| Microsoft.Data.Sqlite | 10.0.11 | Copyright (c) Microsoft Corporation |
| Microsoft.ML.OnnxRuntime | 1.29.0 | Copyright (c) Microsoft Corporation |
| Microsoft.SemanticKernel | 1.79.0 | Copyright (c) Microsoft Corporation |
| System.Security.Cryptography.ProtectedData | 10.0.11 | Copyright (c) Microsoft Corporation |
| Microsoft.Extensions.* (DI, Hosting, Logging, Options) | 10.0.11 | Copyright (c) Microsoft Corporation |
| FastBertTokenizer | 1.0.28 | Copyright (c) Georg Jung |
| Tailwind CSS (compiled output in `wwwroot/css/app.css`) | build output | Copyright (c) Tailwind Labs, Inc. |

### BSD 3-Clause License

| Component | Version | Copyright |
|---|---|---|
| Google.Protobuf | 3.35.1 | Copyright 2008 Google Inc. |

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the conditions of the BSD 3-Clause License are met, including retention of the above
copyright notice, this list of conditions, and the disclaimer; and that neither the name of the
copyright holder nor the names of its contributors may be used to endorse or promote products
derived from this software without specific prior written permission. THIS SOFTWARE IS PROVIDED BY
THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS".

---

## 2. Platform prerequisites (not redistributed)

These are Microsoft components the Windows GUI requires at runtime. They are governed by Microsoft's
own license terms and are **not** redistributed by this project; end users obtain them from
Microsoft. See [`LICENSE.exceptions.md`](LICENSE.exceptions.md) for the AGPL §7 additional
permission that covers linking against them.

| Component | Terms |
|---|---|
| Microsoft Edge WebView2 Runtime | Microsoft Software License Terms / WebView2 redistribution terms |
| Windows App SDK / Windows platform components | Microsoft Software License Terms |

---

## 3. Build- and test-time only (not shipped)

These are used to build or test the project and are not part of any distributed binary. They impose
no obligation on downstream recipients of the application.

| Component | Version | License |
|---|---|---|
| Grpc.Tools (protobuf/gRPC codegen; `PrivateAssets=all`, so it flows to no consumer and ships in no artifact) | 2.83.0 | Apache-2.0 |
| xunit.v3 | 3.2.2 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 |
| Grpc.Core.Testing | 2.46.6 | Apache-2.0 |
| AwesomeAssertions | 9.5.0 | Apache-2.0 |
| Moq | 4.20.72 | BSD-3-Clause |
| bunit | 2.9.0 | MIT |
| AngleSharp | 1.7.1 | MIT |
| coverlet.collector | 10.0.1 | MIT |
| Microsoft.NET.Test.Sdk | 18.8.1 | MIT |
| Microsoft.VisualStudio.Azure.Containers.Tools.Targets | 1.23.0 | Microsoft Software License Terms (build targets only; not redistributable as a standalone offering) |

> **Historical note.** This project previously used **FluentAssertions 8.10.0**, which is licensed
> under the Xceed Community License Agreement — free for non-commercial use only, and restricting
> *use* rather than merely redistribution. It was replaced with **AwesomeAssertions**, the Apache-2.0
> community fork, so that no dependency constrains how this project may be licensed, used, or sold.
> The assertion API is identical; only the root namespace differs. Do not reintroduce
> FluentAssertions 8.x or later.

---

## 4. Data and models obtained at runtime (not redistributed)

These are downloaded onto the end user's machine on first use — by default into `%LOCALAPPDATA%` (the
per-user local app data directory; the exact path is configurable), never into the source tree — and
are not committed to this repository or bundled into any distributed artifact.

| Asset | Source | License |
|---|---|---|
| BGE-large-en-v1.5 ONNX model + tokenizer | Hugging Face, fetched by `OnnxEmbeddingClient` | The upstream model, `BAAI/bge-large-en-v1.5`, is **MIT** and explicitly permits commercial use. The ONNX conversion currently configured by default (`Xenova/bge-large-en-v1.5`) does not publish an explicit license tag; see the caveat below. |
| CodeRouterBench benchmark tables | `huggingface.co/datasets/Lance1573/CodeRouterBench`, synced into the local SQLite database by `BenchmarkSyncService` (Governance → Benchmark Data, the MCP sync tool, or `--sync-benchmark-data`) | **MIT**, per the dataset card |

> **Caveat — embedding model source.** `EmbeddingOptions.ModelUrl` defaults to the `Xenova/`
> re-hosted ONNX conversion, whose model card carries no explicit license tag. The weights derive
> from the MIT-licensed `BAAI/bge-large-en-v1.5`, so the substantive licensing is permissive, but if
> you want an unambiguous provenance chain, point `ModelUrl`/`TokenizerJsonUrl` at a BAAI-published
> artifact or self-host the conversion.

---

## 5. Research attribution

This project implements ideas described in, and consumes a benchmark dataset published alongside,
third-party academic work. That work is **not** authored by or owned by this project:

> Pengfei Zhou, Zhiwei Tang, Yixing Ma, Jiasheng Tang, Yizeng Han, Zhenglin Wan, Fanqing Meng,
> Wei Wang, Bohan Zhuang, Wangbo Zhao, Yang You.
> *Agent-as-a-Router: Agentic Model Routing for Coding Tasks.*
> arXiv:2606.22902. <https://arxiv.org/abs/2606.22902>

The **CodeRouterBench** dataset is published by those authors on Hugging Face under the MIT license.
This repository consumes it; it does not publish or relicense it.

`docs/research/technical-reference.md` is a reading aid summarizing that paper for implementation
purposes. The paper itself remains the copyright of its authors and is distributed under arXiv's
own license terms.
