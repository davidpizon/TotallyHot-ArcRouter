# Plan: #355 benchmark sync tests count a shared temp root

**Status:** Approved by David on 2026-11-24 (Cursor chat: address all open issues).

**Issue:** [#355](https://github.com/davidpizon/TotallyHot-ArcRouter/issues/355).

**Observed cost:** Two failures in one `-parallelMode all` run. Both tests counted every `arcrouter-bench-*` directory in `Path.GetTempPath()` while sibling methods created and deleted their own.

## Change

`BenchmarkSyncService` takes an optional directory factory. Production still calls `Directory.CreateTempSubdirectory`. The two tests create their download directory under a private root and assert that path is gone after `SyncAsync`. They no longer compare directory counts in the shared temp root.
