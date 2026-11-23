# TestWizz — Implementation Plan

**Version:** 2.0 · **Status:** implementation snapshot (2026-11-23) · **Stack:** .NET 10 CLI · **Implemented:** C#/xUnit test proposals, Vue context/planning, review-first UI proposals

## 1. Mission

TestWizz generates **reviewable C#/xUnit test proposals** using a local AI model. It also supports Vue SFC/composable/store context and scenario planning, review-first Playwright proposals, and evidence from reviewed browser journeys. **Vue/Vitest generation and execution are not implemented.** Local model endpoints are private, but source excerpts and UI snapshots are passed to the configured model: use trusted, sanitized data.

```text
pick target ─► gather context ─► plan scenarios ─► generate tests ─► validate ─► (run + repair) ─► review ─► apply
```

### Target goals (some capabilities remain planned)

1. Generate useful happy-path, boundary, negative and exception tests for a selected C# method/class or Vue component/composable/store.
2. Follow the target project's existing conventions (framework, mocking library, naming, folder layout).
3. Work well with **small local models**: tight per-target context, structured JSON output, deterministic pre-/post-processing.
4. Never write proposed tests into the working tree until explicit `apply`; execute generated .NET tests only in isolated containers and reviewed browser journeys only on disposable local fixtures.
5. Be honest: inferred expectations are flagged for review; a green test is not proof of correctness.

### Non-goals (MVP)

Unattended execution of AI-generated browser tests, autonomous browser exploration, production code edits, cloud AI, languages outside supported stacks, and coverage guarantees. Review-first `ui` proposals and reviewed `journey` evidence are supported.

## 2. Key decisions

| ID | Decision |
|----|----------|
| ADR-1 | **Single .NET 10 CLI** (`testwizz`), distributed as a `dotnet tool`. Vue-specific parsing is delegated to a small Node helper script that runs with the **target project's own** `@vue/compiler-sfc`/`typescript`, so TestWizz bundles no JS toolchain. |
| ADR-2 | **Pluggable context providers.** .NET: DocWizz adapter when DocWizz is installed (preferred, reuse don't copy its scanners), Roslyn-based fallback otherwise (a loose compilation of the repo's `.cs` files plus framework references, no MSBuild; NuGet types stay unresolved and are reported). Vue: built-in SFC/TS provider. All map to one `testwizz.context/v1` contract. Missing data is represented as *unavailable*, never guessed. |
| ADR-3 | **Stack-specific capabilities.** C#/xUnit has generation, validation and container-only `run`/repair. Vue supports context and scenario planning but has no generated Vitest proposals or `run`. NUnit/MSTest/Jest are not implemented. |
| ADR-4 | **Local AI only.** Ollama and OpenAI-compatible endpoints restricted to loopback/private IPs (checked at connect time, no proxy, TLS validated when used). API keys only from environment variables; `.env` is never auto-loaded; no public fallback. |
| ADR-5 | **AI output is untrusted.** Structured JSON schema (Ollama `format` / `response_format`), schema + evidence validation, and source comments/docs treated as data, not instructions. |
| ADR-6 | **Proposals first.** `generate` and `ui` write bundles outside the working tree. `run` supports .NET only; dependency restore may use the network, while build/test run offline in Docker/Podman. `apply` is a separate explicit operation with stale-source checks. AI-generated UI tests are not executed by TestWizz. |

### Confirmed stack choices

- **DocWizz** is available and is the primary .NET context source; the Roslyn provider remains as fallback.
- **Docker** is available for isolated `run`.
- **Vue projects:** Vue 3 + TypeScript, Vitest, Pinia (`@pinia/testing`).
- **.NET tests (TestWizz itself and the default for generated tests):** xUnit v3, NSubstitute for mocks, plain xUnit `Assert` (no FluentAssertions; v8+ needs a commercial license). Convention detection still follows whatever a target project already uses (Moq, FluentAssertions, Shouldly…).

## 3. Architecture

```text
src/TestWizz.Cli/          commands, config, JSON output, exit codes
src/TestWizz.Core/         contracts, scenario planner, proposal bundle, validators
src/TestWizz.AI/           Ollama + OpenAI-compatible clients, endpoint guard, prompt rendering
src/TestWizz.DotNet/       Roslyn/DocWizz context, xUnit conventions + generator, dotnet test runner
src/TestWizz.Vue/          Vue target path validation (Vue context helpers currently live in CLI/scripts)
tests/TestWizz.UnitTests/    golden, fake-model, and regression tests
tests/TestWizz.IntegrationTests/    integration tests
tests/TestWizz.E2ETests/          CLI smoke tests
fixtures/dotnet/           calculator, service with DI, exceptions (xUnit v3 + NSubstitute)
fixtures/vue/              TS SFC with props/emits, composable, Pinia store (Vitest)
```

Start with the fewest projects that work; split only when a second consumer appears.

## 4. Contracts

**`testwizz.context/v1`** — repository root + git revision; stack (`dotnet` | `vue`); target identity (symbol/overload, or component/composable/store name); source file, line range, content hash and bounded excerpt; public surface (signature, or props/emits/slots/exposed); dependencies (constructor params, injected services, imported modules, stores, router, HTTP clients); existing related tests (relationship, **not coverage**); requirements if available; list of unavailable fields.

**`testwizz.plan/v1`** — scenarios with `id`, `category` (HappyPath, Boundary, Negative, Exception, StateTransition, Regression), description, expected behavior, evidence references, `source` (`deterministic` | `ai`), `needsReview`.

**`testwizz.proposal/v1`** — proposal id, context/plan hashes, model + prompt-template version, generated files (path, content, linked scenario ids), findings (validation warnings/errors), optional run results.

## 5. Capability boundaries

| Workflow | Implemented | Boundary |
| --- | --- | --- |
| C#/xUnit | Context, scenarios, proposal generation, isolated run/repair, explicit apply, changed-method and bootstrap batches | No automatic source edits or coverage guarantee |
| Vue 3 | SFC/composable/store context and scenario planning | No Vitest generation or runner |
| UI criteria | Review-first Playwright proposals from Markdown or an ADO work item | `testwizz run` supports .NET only |
| ADO journeys | `journey-draft --plan-id` resolves explicit Tested By links and drafts each linked case; `journey` captures approved evidence | Requires a configured local route and optional DocWizz session-state file; no automatic approval, interactive login or per-case route discovery |

For commands use [README](README.md), for setup [Getting started](docs/getting-started.md), and for the ADO → TestWizz → DocWizz workflow [ADO journeys](docs/ado-journeys.md).

## 6. Quality and review gates

- **Generate:** Treat model output and UI snapshots as untrusted. Preserve ADO IDs and explicit links; never infer missing relationships.
- **Validate:** Enforce bounded schemas, evidence, source freshness and path safety. Keep `needsReview` and unsupported cases visible.
- **Execute:** Run generated .NET tests only in constrained containers. Reviewed UI journeys use a fixed interpreter on disposable local fixtures.
- **Publish:** Guarded `apply` is explicit; review journey assertions and screenshots before collecting evidence or publishing user documentation.

## 7. Local model guidance

- Use a code-tuned instruct model that supports JSON/structured output. Candidates to benchmark: Qwen2.5-Coder 7B/14B/32B, DeepSeek-Coder-V2-Lite, Codestral. Pick per hardware; the benchmark (§9) decides the default.
- Small models degrade fast with long prompts: keep context per target and prefer several small calls over one large one.
- Temperature 0–0.2; set `num_ctx` explicitly for Ollama (its default is small).
- Record model name and digest in each proposal so results are reproducible.

## 8. Safety rules (always on)

- Use only loopback/private model endpoints, re-check DNS per connection and bypass proxies. Treat context, UI snapshots, proposals and logs as sensitive. The targeted snapshot exposure assessment remains open in [#11](https://github.com/mycaravam-crypto/testwizz/issues/11).
- Generated .NET tests run only in isolated containers and require an available container runtime. Reviewed `journey` actions can mutate data on a disposable local UI fixture; TestWizz does not run AI-generated UI tests. Proposed test files enter the repository only through explicit guarded `apply`.
- Source text is untrusted prompt input; embedded instructions are ignored and covered by regression tests.
- Timeouts, cancellation and token budgets on every AI call; AI unavailability never corrupts state.

## 9. Verification

- **Unit:** config/arg parsing, endpoint guard, path confinement, scenario extraction, schema validation, hash/staleness checks.
- **Component (offline, CI):** fake AI server (valid, malformed, empty, timeout, prompt injection), fake DocWizz CLI, fake container runtime.
- **Fixture/golden:** `fixtures/dotnet` and `fixtures/vue` produce stable context and plan JSON (timestamps/order normalized).
- **Real local-model acceptance (open):** one trusted UI route, two acceptance criteria, reviewed Playwright assertions and an intentional negative mutation ([#61](https://github.com/mycaravam-crypto/testwizz/issues/61)). Fake-model CI validates the pipeline, not real-model assertion quality.

## 10. Milestones and remaining acceptance

Closing a milestone issue does not imply that every capability in its original roadmap description shipped.

| Milestone | Implemented on `main` | Not implemented / still open | Issues |
|-----------|-----------------------|------------------------------|--------|
| M0 — Foundation | .NET CLI, config, CI and local AI clients | No milestone blocker | #1, #5 closed |
| M1 — .NET slice | C# context, scenario planning, xUnit proposal generation | Generated assertions still require review | #2, #4, #7, #8 closed |
| M2 — Vue slice | SFC/composable/store context and Vue planning | No AI-generated Vitest proposals | #3, #6 closed, narrowed |
| M3 — Run, repair, apply | Isolated .NET/xUnit run and repair; guarded apply | No Vue/Vitest or Playwright runner | #9, #10 closed, narrowed |
| M4 — Workflow | C# `--since`, CI-oriented output, adaptive batch generation | No NUnit/MSTest/Jest adapters or Vue changed-code generation | #12 closed, narrowed |
| UI and ADO | UI proposals, plan-wide ADO drafts from explicit Tested By links, reviewed evidence | Real-model acceptance [#61](https://github.com/mycaravam-crypto/testwizz/issues/61); per-case route discovery and autonomous execution unsupported | #61 open |
| Safety and resilience | Endpoint controls, UI network restrictions, bootstrap checkpoints | Snapshot data exposure [#11](https://github.com/mycaravam-crypto/testwizz/issues/11) and failure/resume regression [#27](https://github.com/mycaravam-crypto/testwizz/issues/27) | #11, #27 open |

Extend framework support only when a concrete use case and acceptance criteria justify it.

## 11. Definition of Done

- Unit, component and fixture tests pass in CI (no real AI or network needed).
- Help text and README updated for new commands.
- Deterministic output for unchanged non-AI input.
- Errors are actionable and contain no secrets.
- No implicit execution, package installation or working-tree changes.
- Uncertainty (`needsReview`, unavailable fields, failing tests) is visible in the output, never hidden.

## 12. Maintenance discipline

1. Keep README commands, architecture and milestone status aligned with working code.
2. Use one focused PR for each validated change; do not imply support for unimplemented adapters.
3. Avoid speculative abstractions or dependencies, and preserve explicit security boundaries.
4. Report CI and real-model acceptance separately. Never weaken assertions or skip tests merely to obtain a green result.
5. Update this plan when the implementation or decisions change.
