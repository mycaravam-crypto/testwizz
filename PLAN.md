# TestWizz — Implementation Plan

**Version:** 2.0 · **Status:** Proposed · **Stack:** .NET 10 CLI · **Targets:** .NET (xUnit) and Vue 3 (Vitest)

## 1. Mission

TestWizz is a local CLI that writes **reviewable unit tests** for .NET and Vue applications using a **local AI model** (Ollama or any OpenAI-compatible server on your machine/LAN). No code leaves your network.

```text
pick target ─► gather context ─► plan scenarios ─► generate tests ─► validate ─► (run + repair) ─► review ─► apply
```

### Goals

1. Generate useful happy-path, boundary, negative and exception tests for a selected C# method/class or Vue component/composable/store.
2. Follow the target project's existing conventions (framework, mocking library, naming, folder layout).
3. Work well with **small local models**: tight per-target context, structured JSON output, deterministic pre-/post-processing.
4. Never touch the working tree until the user runs `apply`; never run code outside an isolated container.
5. Be honest: inferred expectations are flagged for review; a green test is not proof of correctness.

### Non-goals (MVP)

Integration/E2E/browser tests, editing production code, cloud AI, languages other than C# and TypeScript/JavaScript (Vue), coverage guarantees.

## 2. Key decisions

| ID | Decision |
|----|----------|
| ADR-1 | **Single .NET 10 CLI** (`testwizz`), distributed as a `dotnet tool`. Vue-specific parsing is delegated to a small Node helper script that runs with the **target project's own** `@vue/compiler-sfc`/`typescript`, so TestWizz bundles no JS toolchain. |
| ADR-2 | **Pluggable context providers.** .NET: DocWizz adapter when DocWizz is installed (preferred, reuse don't copy its scanners), Roslyn-based fallback otherwise (a loose compilation of the repo's `.cs` files plus framework references, no MSBuild; NuGet types stay unresolved and are reported). Vue: built-in SFC/TS provider. All map to one `testwizz.context/v1` contract. Missing data is represented as *unavailable*, never guessed. |
| ADR-3 | **Stack adapters**, not a generic framework: `DotNet` (xUnit first; NUnit/MSTest later) and `Vue` (Vitest + `@vue/test-utils` first; Jest later). Each adapter owns convention detection, prompt templates, static validation and run commands. |
| ADR-4 | **Local AI only.** Ollama and OpenAI-compatible endpoints restricted to loopback/private IPs (checked at connect time, no proxy, TLS validated when used). API keys only from environment variables; `.env` is never auto-loaded; no public fallback. |
| ADR-5 | **AI output is untrusted.** Structured JSON schema (Ollama `format` / `response_format`), schema + evidence validation, and source comments/docs treated as data, not instructions. |
| ADR-6 | **Proposals first.** `generate` writes a proposal bundle outside the working tree. `run` executes tests only inside a container (Docker/Podman, network disabled). `apply` is a separate explicit command with stale-source checks. |

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
src/TestWizz.Vue/          Node helper (scripts/vue-context.mjs), Vitest conventions + generator, vitest runner
tests/TestWizz.UnitTests/
tests/TestWizz.ComponentTests/   fake DocWizz, fake AI server, fake container runtime
fixtures/dotnet/           calculator, service with DI, exceptions (xUnit v3 + NSubstitute)
fixtures/vue/              TS SFC with props/emits, composable, Pinia store (Vitest)
```

Start with the fewest projects that work; split only when a second consumer appears.

## 4. Contracts

**`testwizz.context/v1`** — repository root + git revision; stack (`dotnet` | `vue`); target identity (symbol/overload, or component/composable/store name); source file, line range, content hash and bounded excerpt; public surface (signature, or props/emits/slots/exposed); dependencies (constructor params, injected services, imported modules, stores, router, HTTP clients); existing related tests (relationship, **not coverage**); requirements if available; list of unavailable fields.

**`testwizz.plan/v1`** — scenarios with `id`, `category` (HappyPath, Boundary, Negative, Exception, StateTransition, Regression), description, expected behavior, evidence references, `source` (`deterministic` | `ai`), `needsReview`.

**`testwizz.proposal/v1`** — proposal id, context/plan hashes, model + prompt-template version, generated files (path, content, linked scenario ids), findings (validation warnings/errors), optional run results.

## 5. CLI

```bash
testwizz init                         # write testwizz.json (model endpoint, stack hints)
testwizz ai check                     # verify local model is reachable and returns valid JSON
testwizz context <path> --for <target> [--format json]
testwizz plan    <path> --for <target>
testwizz generate <path> --for <target> [--out <dir>]   # context + plan + tests → proposal
testwizz run      --proposal <id>     # container-only build/test + bounded repair
testwizz apply    --proposal <id>     # copy files into repo after stale checks
testwizz generate <path> --since origin/main            # later: changed targets only
```

Targets: `OrderService.PlaceOrder`, `OrderService` (all public methods), `src/components/CartItem.vue`, `src/composables/useCart.ts`, `src/stores/cart.ts`.

`--format json` writes one versioned JSON object to stdout; diagnostics go to stderr. Exit codes: `0` success, `1` validation/test failure, `2` configuration or runtime error.

Example `testwizz.json`:

```json
{
  "ai": { "provider": "ollama", "endpoint": "http://127.0.0.1:11434", "model": "qwen2.5-coder:14b",
          "apiKeyEnv": null, "timeoutSeconds": 120, "maxContextTokens": 16000 },
  "run": { "runtime": "docker", "maxRepairAttempts": 2 },
  "output": "../.testwizz-proposals"
}
```

## 6. Workflow

1. **Context.** Resolve the target; on ambiguity return candidate ids. Keep only what the model needs: the target's source, signatures (not bodies) of its dependencies, one or two nearby existing tests as style examples. Respect `.gitignore`; cap size by token budget.
2. **Conventions.** .NET: test project referencing the target project, xUnit version, mocking lib (Moq/NSubstitute/FakeItEasy), assertion lib (FluentAssertions/Shouldly/plain), naming pattern. Vue: `vitest` config, `@vue/test-utils`, `@pinia/testing`, `happy-dom`/`jsdom`, test file location (`__tests__/` vs `*.spec.ts`). Never add packages; report what's missing.
3. **Plan.** Deterministic extraction first (guard clauses, `throw`, `switch`/`if` branches, comparisons, props validators, `emit` calls, `watch`/computed). The AI then enriches the plan with structured output. Expectations derived only from current code are marked `needsReview`.
4. **Generate.** One prompt per target (or per method for large classes) using a versioned template per stack, plan + context + style example, JSON schema output `{ files[], scenarioIds, assumptions }`.
5. **Static validation.** Schema and scenario links; path confinement (no traversal or symlink escapes); no overwrite of existing files; C# parsed with Roslyn; TS/Vue parsed via the Node helper; references to non-existent members flagged; empty/placeholder/skipped assertions flagged.
6. **Run + repair (opt-in).** Copy repo + proposal into a container (`mcr.microsoft.com/dotnet/sdk` or `node`), restore dependencies, then run `dotnet test` / `vitest run` **with network disabled**. Parse results (TRX / Vitest JSON reporter). On compile or test failure feed diagnostics back to the model at most `maxRepairAttempts` times; repairs may only change generated test files and may not delete assertions or add skips. If a test still fails, keep it in the proposal, marked as a possible bug *or* bad test.
7. **Review + apply.** Proposal bundle contains `manifest.json`, `plan.md`, test files, `changes.patch`, `findings.md`. `apply` refuses if source hashes changed or a target file already exists.

## 7. Local model guidance

- Use a code-tuned instruct model that supports JSON/structured output. Candidates to benchmark: Qwen2.5-Coder 7B/14B/32B, DeepSeek-Coder-V2-Lite, Codestral. Pick per hardware; the benchmark (§9) decides the default.
- Small models degrade fast with long prompts: keep context per target and prefer several small calls over one large one.
- Temperature 0–0.2; set `num_ctx` explicitly for Ollama (its default is small).
- Record model name and digest in each proposal so results are reproducible.

## 8. Safety rules (always on)

- Only loopback/private endpoints; DNS re-checked per connection; proxies bypassed for model calls; no secrets in logs, prompts or output.
- No writes to the working tree except via `apply`; no execution outside a container; fail closed if no container runtime.
- Source text is untrusted prompt input; embedded instructions are ignored and covered by regression tests.
- Timeouts, cancellation and token budgets on every AI call; AI unavailability never corrupts state.

## 9. Verification

- **Unit:** config/arg parsing, endpoint guard, path confinement, scenario extraction, schema validation, hash/staleness checks.
- **Component (offline, CI):** fake AI server (valid, malformed, empty, timeout, prompt injection), fake DocWizz CLI, fake container runtime.
- **Fixture/golden:** `fixtures/dotnet` and `fixtures/vue` produce stable context and plan JSON (timestamps/order normalized).
- **Model benchmark (manual/nightly, local):** per model, on both fixture sets: compile/parse rate, pass rate after repair, assertion quality, unsupported-assertion ratio, optional mutation score (Stryker.NET / StrykerJS). Test count alone is not quality.

## 10. Milestones

Each milestone ends with something a developer can actually use.

| Milestone | Delivers | Issues |
|-----------|----------|--------|
| **M0 — Foundation** | CLI skeleton, config, CI; local AI client + `ai check` | #1, #5 |
| **M1 — .NET slice** | `context` / `plan` / `generate` for C# → xUnit proposal bundle with static validation | #2, #4, #7, #8 |
| **M2 — Vue slice** | Same flow for Vue SFCs, composables and Pinia stores → Vitest proposals | #3, #6 |
| **M3 — Run, repair, apply** | Container-isolated `run` for both stacks, bounded repair loop, guarded `apply` | #9, #10 |
| **M4 — Workflow** | `--since` changed-code mode, CI report output, more frameworks (NUnit/MSTest/Jest) | #12 |
| Cross-cutting | Quality benchmark + security regression suite, grows each milestone | #11 |

## 11. Definition of Done

- Unit, component and fixture tests pass in CI (no real AI or network needed).
- Help text and README updated for new commands.
- Deterministic output for unchanged non-AI input.
- Errors are actionable and contain no secrets.
- No implicit execution, package installation or working-tree changes.
- Uncertainty (`needsReview`, unavailable fields, failing tests) is visible in the output, never hidden.

## 12. Notes for Claude Code

1. Implement one issue per PR, starting with #1; link the issue in the PR.
2. Before writing the DocWizz adapter, inspect the installed DocWizz's real output; don't invent fields. If DocWizz is absent, the Roslyn fallback is the path.
3. Avoid speculative abstractions and unjustified dependencies.
4. Report build/test results honestly; never delete assertions or skip tests to get green.
5. Update this plan when a decision changes.
