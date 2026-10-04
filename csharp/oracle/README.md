# Archived Go oracle

The reference backend is pinned to `f29aeb9f825d96feea27841f3f7342dbf0df68a8` and Go 1.27.1. It is retained for explicit compatibility investigations. Product builds, tests, packages and CI do not run it or fall back to it.

`build/` preserves the former Go build and workflow definitions as historical reference. `validate.mjs` extracts the pinned Git revision into a fresh directory under `built/csharp/archived-oracle/`, records its archive hash, and runs its Go tests with automatic toolchain downloads disabled:

```powershell
node csharp/oracle/validate.mjs --go C:/path/to/go.exe
node csharp/oracle/refresh-data.mjs --go C:/path/to/go.exe --check
```

The second command verifies `csharp/data/` against the pinned Unicode, normalization, regexp, locale and enum implementations. Omitting `--check` explicitly refreshes those snapshots. Review snapshot changes, update `csharp/data/manifest.json`, regenerate the product tables, and rerun compatibility comparisons together when changing the pin. The extraction requires the pinned Git object to exist locally.

The `tsc/` and `tools/` trees still contain reference implementation text, shared schemas, standard libraries, fixtures, licenses and historical baselines. Node generators read the shared data without a Go compiler. This preserves provenance and existing test inputs while keeping the Go toolchain outside the product dependency graph.
