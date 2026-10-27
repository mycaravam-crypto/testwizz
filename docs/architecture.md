# Architecture

TestWizz is a .NET CLI that generates reviewable test proposals. The repository is organized around dependency boundaries:

- `TestWizz.Core`: stack-neutral configuration, contexts, plans, proposal bundles, and execution contracts.
- `TestWizz.AI`: guarded local-model clients, structured-output validation, and scenario enrichment.
- `TestWizz.DotNet`: Roslyn extraction, xUnit generation, validation, and container-based execution.
- `TestWizz.Vue`: Vue target path validation boundary. Context extraction currently remains integrated with the CLI and its bundled Node helpers; full migration is planned.
- `TestWizz.Cli`: command dispatch, orchestration, output and Vue helper integration.

Dependency direction: CLI orchestrates adapters and Core; adapters depend on Core where needed. The local AI endpoint must be private, and test execution runs in a network-disabled container after restore.

## Architecture decisions

1. Keep model output as untrusted proposals. Review and validation are mandatory before application.
2. Keep framework-specific extraction separate from Core to preserve maintainability under ISO/IEC 25010.
3. Avoid automatic merges, package publication, or repository modifications from the release workflow.
4. Treat syntactic evidence and related-test discovery as insufficient to claim runtime coverage.

## Batch execution and performance\n\n`bootstrap --all` reuses one Roslyn context provider and local model client across targets, but generates each target sequentially to preserve checkpoint semantics. `generate --since <ref> --all-changed` executes bounded concurrent tasks against the shared read-only compilation and model HTTP client. It ramps from one worker to `--max-workers` (default 4, maximum 8) based on completed-target throughput, with a regression backoff. This heuristic is not a model-token benchmark and should be profiled on the actual LLM hardware. Debug test-source output is opt-in and written to stderr; model output remains untrusted and review-first.\n\n## Future Vue integration

Move the Vue context and scenario providers behind the `TestWizz.Vue` boundary while retaining CLI compatibility and existing golden fixture behavior. AI-powered Vue/Vitest generation is not implemented yet.
