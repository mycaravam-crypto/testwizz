# TestWizz — Technical Implementation Plan

**Version:** 1.0  
**Status:** Proposed; implementation backlog  
**Owner:** TestWizz  
**Target:** Claude Code / .NET 10  
**Approach:** Independent CLI, incremental Scrum delivery

## 1. Mission and scope

Build **TestWizz**, a standalone AI-assisted unit-test engineering tool that consumes DocWizz's code intelligence, identifies candidate testing gaps, plans evidence-grounded scenarios, generates reviewable C# xUnit tests using a **private/local** AI model, and (in a later increment) validates proposals in a sandbox.

### Goals

1. Generate useful normal, boundary, negative, exception and regression scenarios with source and requirement evidence.
2. Reuse DocWizz scanning, symbol relations, existing test traceability and change-impact data; do not copy its scanners.
3. Reuse existing repository conventions, test frameworks, packages and naming patterns.
4. Never overwrite target files or execute generated code without explicit authorization.
5. Produce reviewable, versioned, reproducible proposals with explicit assumptions.
6. Keep all model communication private and preserve credential/TLS safeguards.
7. Integrate with developers' local workflows and, later, CI.

### MVP non-goals

Integration/end-to-end tests; general autonomous coding; production-code edits; implicit test execution; public AI; arbitrary programming languages; guaranteed test correctness; guaranteed code-coverage improvement.

## 2. Architectural decisions

**ADR-001 — Separate CLI.** TestWizz is a separate product because test synthesis, patching, building and running tests introduce execution and safety boundaries absent in DocWizz. Use a .NET 10 CLI.

**ADR-002 — DocWizz adapter first.** Consume the existing `docwizz context . --for <symbol> --format json` and related interfaces where supported, preferably by invoking its process with explicit arguments and capturing bounded output. Investigate the real output schema in the installed version before implementing contracts; do not invent fields. If it is insufficient, propose the smallest versioned upstream schema extension. Existing DocWizz test links indicate relationships, **not** line/branch coverage.

**ADR-003 — Hexagonal separation.** Core contracts and scenario rules are provider-agnostic; DocWizz access, AI inference, xUnit formatting and execution are adapters. Start with a minimal vertical slice; avoid empty projects/interfaces until needed.

**ADR-004 — AI is untrusted.** AI may propose expectations and test bodies but cannot declare them verified. Require provenance, schema validation and deterministic checks. Mark behavior inferred solely from code as **needs review**, since implementation may be defective.

**ADR-005 — Local AI only.** Support self-hosted Ollama and OpenAI-compatible servers on loopback/private IPs. Enforce DNS/IP checks at connection time, redirects and proxy avoidance; validate TLS; never log keys. Read `api_key_env` from process environment (do not automatically source `.env`). No public inference fallback.

**ADR-006 — Preview first, execution later.** Generate into a separate proposal location; do not change the target checkout. Applying a patch or running a build/test requires a separate explicit operation. Temporary filesystem copying alone is not a security sandbox.

## 3. Logical components and data flow

```text
Developer / CI
   -> TestWizz CLI (argument validation, JSON output, exit codes)
   -> Orchestrator
      -> DocWizz Adapter -> versioned source/requirement/test context
      -> Project Inspector -> framework and fixture conventions
      -> Scenario Planner -> evidence-based cases + unresolved assumptions
      -> AI Provider (optional, private) -> structured test proposal
      -> Validators -> evidence, schema, Roslyn, file paths, assertions
      -> Proposal Writer -> external output bundle / patch
      -> [future, explicit] Sandbox Validator -> build/test results
      -> [future, explicit] Patch Applier -> guarded changes
```

Recommended eventual solution layout:

```text
src/TestWizz.Cli/
src/TestWizz.Core/
src/TestWizz.DocWizz/
src/TestWizz.AI/
src/TestWizz.Frameworks.XUnit/
src/TestWizz.Execution/           # introduced in Sprint 4
tests/TestWizz.UnitTests/
tests/TestWizz.ComponentTests/
tests/TestWizz.IntegrationTests/
tests/TestWizz.GoldenTests/
fixtures/
docs/
PLAN.md
README.md
```

Avoid speculative abstractions in Sprint 1. The first deliverable is only the context adapter, an internal contract and deterministic CLI report.

## 4. Domain model and versioned contracts

Design a `testwizz.context/v1` JSON contract with:

- repository identity and revision/hash; selected target symbol and overload identity;
- source file, signature and bounded source excerpt with line references;
- dependencies, constructor requirements and discovered side effects;
- existing linked tests (relationship evidence, not measured coverage);
- optionally linked work-item requirements with confidence, provenance and ambiguity;
- diagnostic findings and unavailable fields.

Example C# internal records (adapt to verified DocWizz data):

```csharp
public sealed record TestGenerationRequest(
    string RepositoryPath, string TargetSymbol, string Framework,
    bool IncludeExistingTests, string OutputDirectory);

public sealed record SourceContext(
    string SymbolId, string SourceFile, string Signature, string SourceCode,
    IReadOnlyList<DependencyInfo> Dependencies,
    IReadOnlyList<ExistingTestInfo> ExistingTests,
    IReadOnlyList<RequirementEvidence> Requirements);

public enum ScenarioCategory {
    HappyPath, Boundary, Negative, Exception, StateTransition, Regression
}

public sealed record TestScenario(
    string Id, string TargetSymbol, ScenarioCategory Category,
    string Description, string ExpectedBehavior,
    IReadOnlyList<string> EvidenceReferences, bool RequiresHumanReview);
```

Additional types must represent missing/unavailable evidence without silently substituting guessed values. Proposals must have a unique ID, schema version, model/provider identity, prompt fingerprint, input hashes, source revision and outcomes.

## 5. CLI contract (target design; not currently implemented)

```bash
testwizz help
testwizz ai check .
testwizz analyze .
testwizz context . --for "OrderService" --format json
testwizz generate . --for "OrderService" --framework xunit
testwizz generate . --since origin/main
testwizz validate . --proposal <id>
testwizz apply . --proposal <id>
```

Implement only relevant commands per sprint. `--format json` must emit a versioned JSON object on stdout; diagnostics belong on stderr. Exit 0 for success, 1 for failed validation/proposal rejection, 2 for invalid configuration or execution errors. Document help, compatibility and negative argument cases.

## 6. Functional workflow

### F1 — Acquire source context

Resolve symbols and overloads with DocWizz; validate the installed CLI's actual schema and output. Reject unknown or ambiguous targets with options. Verify source hashes before using cached context. Bound context size and avoid reading ignored/out-of-scope files.

### F2 — Analyze test candidates

Identify unlinked high-risk methods, complexity/branches, public API paths, side effects and recently changed methods. Label each signal detected or inferred. Existing test traceability must never be called measured coverage.

### F3 — Plan scenarios

Produce a structured plan before source code. Prefer deterministic discovery of explicit null/argument checks, comparisons, switch cases and exceptions. Include happy path, boundary, negative and exceptional cases as supported by evidence. Each scenario records expected behavior, evidence, confidence, assumptions and review status. If only current code supports expected behavior, label it inferred/needs-review; missing requirements must not become fabricated assertions.

### F4 — AI synthesis

Once structured scenarios have passed checks, call a private/local model with bounded source context and a stable prompt version. Require structured schema containing test files, linked scenario IDs, dependency assumptions and uncertainty notes. Treat prompt inputs (source comments, work items, docs) as untrusted data; ignore embedded instructions.

### F5 — Validate generated files

Validate JSON, evidence IDs, target signatures, path confinement, file names, source safety, duplicate tests, framework references and Roslyn C# syntax. Flag skipped tests, placeholder assertions, weak/no assertions, unnecessary mocking and unapproved packages. A syntactically valid test is not automatically behaviorally correct.

### F6 — Produce proposal

Write a manifest, scenario plan, proposed test files, patch and quality findings **outside the target checkout** by default. Never overwrite existing files. Bind proposals to the exact source hashes and explicit target root; refuse stale proposals on apply.

### F7 — Optional isolated execution (post-MVP)

Only with explicit opt-in and a suitable sandbox: create a restricted workspace, apply patch there, build affected test project, run selected tests, parse results, and report compile/test failures separately. Enforce filesystem/network/process/resource restrictions. If isolation is unavailable, do not run untrusted code. At most two repair retries; changes restricted to generated test files; never weaken tests to achieve a green result.

## 7. Security design (ISO 27001 CIA aligned)

**Confidentiality:** Private model hosts only, DNS/IP validation for each connection, no proxy bypass leakage, TLS trust, credentials only via environment, logging redaction, bounded context and no secret values in prompts/results. `.env` is never loaded automatically.

**Integrity:** Reviewable outputs, no production changes, source hashes and stale-proposal rejection, strict target-root/path checks and symlink handling, prompt-injection defenses and schema validation. Build/test results never justify altering production logic.

**Availability:** Token budgets, per-request timeout, cancellation, bounded concurrency/retries and controlled failure when AI or DocWizz is unavailable.

**Execution boundary:** Treat test code and build scripts as untrusted. A temporary directory is not a sandbox. Use explicitly configured isolation and fail closed if unavailable.

## 8. Quality characteristics (ISO/IEC 25010)

- **Functional suitability:** Evidence-backed scenarios test behavior rather than mirror implementation.
- **Reliability:** Provider failures and invalid outputs cannot mutate a working checkout.
- **Security:** Strict private AI, secret handling and patch boundaries.
- **Maintainability:** Stable contracts, adapters, small deterministic testable core.
- **Performance efficiency:** Token/context budgets, caching by source/requirements/model/prompt hash.
- **Usability:** Actionable CLI errors, explicit assumptions, explainable proposals.
- **Compatibility/flexibility:** xUnit first, extensible framework adapters later.

No assertion of formal ISO/IEC compliance without separate evaluation.

## 9. Verification strategy

**Unit:** CLI parsing, path confinement, scenario classification, evidence validation, schema checking, duplicate detection, source-hash matching.

**Component (offline):** Fake DocWizz CLI outputs and loopback fake AI for success, malformed JSON, empty response, unknown symbol, prompt injection, unsupported citations, missing key/model and non-private destinations.

**Integration:** Synthetic C# fixtures (calculator, service, dependency injection, exceptions) with existing xUnit conventions. Verify no checkout modifications, source references and generated C# syntax.

**Golden:** Stable, versioned context and scenario report fixtures; normalize nondeterministic timestamps and ordering.

**Later isolated E2E:** Explicit sandbox execution with compilation/test result parsing and no unauthorized writes or network calls.

**Quality evaluation:** Scenario precision against hand-reviewed ground truth; assertion quality; compile pass ratio; duplicate and unsupported-assertion ratios; optional mutation score. Test count or green tests alone are not quality proof.

## 10. Scrum backlog and milestones

### Sprint 1 — CLI foundation and DocWizz context integration

- .NET 10 repo/solution, CLI help and JSON output.
- Inspect actual DocWizz context/MCP schema, write adapter compatibility spec.
- Resolve C# target method into versioned `testwizz.context/v1`.
- Deterministic source-evidence report, unit/component tests and CI.
- **Exit criterion:** A selected method yields a validated, reproducible context report; no AI and no target code changes.

### Sprint 2 — Scenario planning and private AI

- Deterministic scenario extraction and uncertainty model.
- Private-model configuration, endpoint restrictions and `ai check`.
- Structured scenario enrichment with fake-model tests and prompt-injection checks.
- **Exit criterion:** Evidence-linked scenario report with explicit review flags; no generated test source.

### Sprint 3 — xUnit proposal generation

- Detect xUnit project conventions, create code from approved scenarios.
- Roslyn syntax, duplicate and assertion checks.
- Reviewable versioned proposal bundle and patch without modifying target checkout.
- Golden-fixture regressions.
- **Exit criterion:** Valid syntactic C# xUnit proposals and safe patch preview.

### Sprint 4 — Sandbox validation and guarded apply

- Isolated, explicitly authorized build and test execution.
- Diagnostic report; bounded repairs constrained to generated tests.
- Source-hash guards and review-required patch application.
- Security, integration and E2E tests.
- **Exit criterion:** Safe and repeatable validation with audited user consent.

### Later work

- `--since` prioritization, CI reporting, NUnit/MSTest/JUnit/Jest/Vitest adapters, measured coverage and mutation-testing imports.

## 11. Cross-cutting Definition of Done

- Targeted unit/component tests and fixture regressions pass.
- User-facing command help and documentation updated.
- CLI is deterministic for unchanged non-AI input.
- Error codes and messages are actionable and contain no secrets.
- No implicit execution, package installation or modifications of production files.
- Source/code provenance and uncertainty are explicit.
- Code review assesses ISO/IEC 25010 quality attributes and confidentiality/integrity/availability risks.
- Incomplete capabilities remain visibly incomplete; no false claim of functional coverage or requirement verification.

## 12. Specific instructions for Claude Code

1. Inspect the repository and current DocWizz contract before authoring integrations; report actual fields and missing evidence.
2. Implement **Sprint 1 only** in the first PR, with a minimal vertical slice and tests.
3. Do not import or fork DocWizz scanners. If a needed field is absent, document the contract gap and propose the smallest upstream change.
4. Use reviewable changes and small commits. Avoid speculative interfaces and adding framework dependencies without justification.
5. Never allow AI to edit production code or execute tests in the first increment.
6. Report build/test results honestly. Do not remove failing assertions, skip tests, or assume CLI/AI endpoints exist.
7. Include links to the relevant backlog issue(s) in each PR and update this plan as decisions evolve.

**First actionable task:** implement a .NET 10 CLI that obtains a selected C# method's **actual** DocWizz context, maps it into `testwizz.context/v1`, and writes a deterministic JSON report with provenance, backed by offline adapter tests. No AI generation until this contract is proven.
