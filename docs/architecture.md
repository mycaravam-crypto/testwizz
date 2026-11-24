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
3. The SemVer workflow creates a tag and publishes a .NET tool package after a merged PR unless labeled `release:none`. It does not automatically merge PRs or modify application source.
4. Treat syntactic evidence and related-test discovery as insufficient to claim runtime coverage.

## Batch execution and performance

`bootstrap --all` reuses one Roslyn context provider and local model client across targets and generates them on a bounded, continuously replenished worker queue; checkpoint writes stay on the coordinator. `generate --since <ref> --all-changed` uses the same queue against the shared read-only compilation and model HTTP client. Without an explicit `--max-workers`, both batches ramp from one worker towards the default maximum of 4 using a rolling mean of windowed completion throughput with hysteresis (rise above +10%, back off below −15% and hold), so a single noisy window does not change concurrency; an explicit `--max-workers N` (1-8) uses N. Bootstrap retries transient model failures a bounded number of times (`--retries`) and records sanitized failure reasons in its checkpoint. This heuristic is not a model-token benchmark and should be profiled on the actual LLM hardware. Debug test-source output is opt-in and written to stderr; model output remains untrusted and review-first.

## ADO journey trust boundary

`journey-draft --plan-id` exports the ADO plan and trace read-only, validates project/plan identity and uses **explicit Tested By links** without inferring missing relationships. The batch reuses one bounded UI snapshot and local-model connection; every response is validated and saved externally as a review-required draft. For protected routes, the browser reads a repository-confined DocWizz Playwright state file (cookies, localStorage and sessionStorage), without ever sending the state to the AI or writing it into proposals. It fails closed on login redirects and rejected APIs; the read-only snapshot still blocks cross-origin requests. The separate `journey` command captures only approved, repository-relative declarative steps against disposable local UI data. DocWizz imports validated evidence. See [ADO journeys](ado-journeys.md).

## Future Vue integration

Move the Vue context and scenario providers behind the `TestWizz.Vue` boundary while retaining CLI compatibility and existing golden fixture behavior. AI-powered Vue/Vitest generation is not implemented yet.

## Bounded batch execution

Model requests use a replenished bounded queue, independent container verification uses `--verify-workers` and each test still has a separate ephemeral workspace. Checkpoints remain coordinator-owned.
