# testwizz

Generate reviewable **C#/xUnit test proposals** with a local AI model (Ollama or an OpenAI-compatible server). TestWizz also offers **Vue 3 context and scenario planning** and **review-first Playwright UI proposals**. Model endpoints are limited to private networks; use sanitized source and UI data, as these are supplied to the configured model.

Status: active development. C#/xUnit generation and isolated .NET execution, Vue context/planning, review-first UI proposals, **ADO plan-wide journey drafts**, and reviewed journey evidence are implemented. **Vue/Vitest generation and Vue/UI execution by `testwizz run` are not implemented.** See [Getting started](docs/getting-started.md), [ADO journeys](docs/ado-journeys.md), and the [implementation plan](PLAN.md).

## Commands (implemented so far)

| Command | Description |
|---------|-------------|
| `testwizz help [command]` | Show available commands or help for one command. |
| `testwizz init [path] [--force]` | Create a default `testwizz.yaml` in a directory. Refuses to overwrite without `--force`. |
| `testwizz config [path]` | Validate `testwizz.yaml` or legacy `testwizz.json` and show the effective configuration. |
| `testwizz ai check [path]` | Check that the configured model is reachable, private, installed and returns schema-valid JSON. |
| `testwizz context [path] --for <target>` | Show `testwizz.context/v1` for a C# type/method or a Vue SFC, composable or Pinia store. |
| `testwizz plan [path] --for <method> [--ai]` | Plan evidence-linked C# or Vue scenarios (`testwizz.plan/v1`); `--ai` lets the local model propose more. |
| `testwizz generate [path] --for <method> [--out <dir>] [--ai-plan]` or `testwizz generate [path] --since <ref> [--list-changed] [--for <method>]` | Plan, let the local model write xUnit tests, validate them and save a proposal bundle outside the repository. The repository is not changed. |
| `testwizz ui [path] --url <loopback-url> (--criteria <file.md> \| --ado-workitem <id>)` | Generate review-first Playwright UI proposals from a local route and explicit Markdown or Azure DevOps work-item acceptance criteria; does not execute tests. |
| `testwizz journey [path] --url <loopback-url> --workitem <id> --plan <ado-plan.json> --case <id> --steps <reviewed.json>` | Capture step screenshots and assertion evidence from a reviewed ADO test case; does not execute AI-generated code. |
| `testwizz journey-draft [path] --plan-id <id> [--url <loopback-url>] [--area <path>] [--list-only]` or `--workitem <id> --plan <ado-plan.json> --case <id> --url <loopback-url>` | Automatically discovers explicit ADO Tested By links per plan and generates review-required AI journey drafts, or handles a single case. Nothing is executed. |
| `testwizz run [path] --proposal <id> [--no-repair] [--image <image>] [--timeout <s>]` | Build and run **.NET/xUnit proposals only** in Docker/Podman. Restore can access package feeds; build/test are network-disabled. Bounded AI repair updates the proposal bundle, not the repository. Vue/UI proposals are unsupported. |
| `testwizz preflight [path] [--image <image>] [--skip-network]` | Optional check before `run`/`bootstrap`, reported per stage: container runtime, workspace paths, whether the effective SDK image starts, and whether NuGet restores from inside it with TLS validation. Runs no repository code; credentials are redacted. See [Corporate environments](docs/corporate-environments.md). |
| `testwizz apply [path] --proposal <id> [--allow-unverified]` | Copy a proposal's new test files into the repository after stale and overwrite checks. Nothing is committed. |

## Azure DevOps journeys and documentation

Start from an entire ADO test plan instead of entering individual work-item IDs. TestWizz reads **explicit Tested By links**, proposes browser steps with your **local model**, and saves review-required drafts:

```bash
# Run in the target application's repository with a trusted local app running.
ado auth status
testwizz ai check .
export TESTWIZZ_JOURNEY_URL=http://127.0.0.1:5173/orders
# If the application requires JWT: reuse the DocWizz Playwright login state.
export TESTWIZZ_STORAGE_STATE=.auth/docwizz-state.json
testwizz journey-draft . --plan-id 12 --list-only   # inspect links without AI/browser
testwizz journey-draft . --plan-id 12               # draft linked cases
testwizz journey-draft . --plan-id 12 --resume      # restart after interruption
```

A shared loopback start URL is required (environment variable or `--url`); ADO does not reliably provide a route per case. For protected pages, `--storage-state .auth/docwizz-state.json` (or `TESTWIZZ_STORAGE_STATE`) reuses the same Playwright cookies, localStorage and extended sessionStorage as DocWizz screenshots; the JWT stays out of the model prompt. Unlinked cases are skipped, multiple explicit links remain separate, and unsupported steps are reported. **Drafts are never executed or applied automatically.** Follow [ADO journeys](docs/ado-journeys.md) to review, capture screenshot evidence, and publish a DocWizz manual.

For a single work item, `testwizz ui . --url http://127.0.0.1:5173/orders --ado-workitem 4711` creates review-first Playwright proposals; `testwizz run` cannot execute UI proposals. The original single-case `journey-draft` form remains supported (see `testwizz help journey-draft`).

**Data boundary:** ADO criteria, case steps and UI accessibility text are sent to the configured private model endpoint. Use sanitized data and trusted test applications.

## Applying verified proposals in a batch

After generating and running proposals, use:

```bash
testwizz apply . --all --verified-only
testwizz apply . --all --allow-unverified
testwizz apply . --all --verified-only --out ../my-proposals --format json
```

The command processes proposal bundles in deterministic directory-name order. It applies only proposals with a successful last test run and unchanged generated files. Existing source-staleness checks and destination-file safeguards remain in effect. Invalid, unverified or stale proposals are **skipped**, while destination-path conflicts are reported as **conflicts**. Each proposal has an individual outcome and reason; the summary includes applied, skipped and conflicted counts plus the number of files added. Conflicts cause exit code `1`; skipped proposals alone do not. No Git commits are created. A second invocation will report already-applied files as conflicts (no overwrite).

`--all` requires exactly one of `--verified-only` and `--allow-unverified`; neither mode can be combined with `--proposal`. The unverified mode bypasses the successful-run requirement, but retains manifest validity, source freshness, path safety and no-overwrite checks. Use `testwizz run . --proposal <id>` to verify proposals before applying them.

## Batch performance and debug output

Changed-method generation supports adaptive concurrency:

```bash
testwizz generate . --since main --all-changed --max-workers 4 --debug
testwizz generate . --since main --all-changed --resume --max-workers 2
testwizz bootstrap . --all --generate-only --debug
```

- `--max-workers N` (1-8, default maximum 4) applies to `bootstrap` and `generate --all-changed`. Without an explicit value, the scheduler starts with **one** model request and adapts: completions are grouped into windows of the current worker count, each level is judged on a **rolling mean of its last three windows** (targets/s), and **hysteresis** prevents oscillation — a level rises only when it beats the level below by more than 10%, a plateau keeps the current level, and it falls only when throughput drops more than 15% below the level below, after which it holds for three windows before probing again. A single slow or fast window never changes the level. When `--max-workers N` is explicitly given, it uses up to N simultaneous requests immediately. Tune for your local model/GPU; more parallel requests do not always mean faster inference.
- The changed-code batch loads one Roslyn context provider and one reusable model client instead of reloading these per target.
- `bootstrap --all` and `generate --all-changed` use bounded **work-conserving concurrency**: a completed request immediately frees a slot for the next target instead of waiting for the slowest request in a batch. Explicit `--max-workers 2` starts two model requests immediately; the setting is a concurrency limit, not guaranteed speedup on a single-GPU model. A shared model client queries model metadata at most once per batch.
- **Generation and verification overlap.** `bootstrap --all` verifies each proposal as soon as its generation finishes, while the model is still working on other targets, so total time approaches the longer of the two instead of their sum. Recovered proposals are verified straight away. The progress bar shows generation until the last model request finishes, then switches to verification with the targets already verified counted; `--progress-jsonl` `generated` and `finalized` events can interleave, and each phase's `completed` counter only grows.
- `bootstrap --all --verify-workers 2` optionally verifies two proposals concurrently in **independent disposable Docker/Podman workspaces** (1-4 allowed, default 1). This never shares generated source, build outputs, or writable NuGet caches between containers. Runtime availability is checked once per batch, the first time a proposal needs it, instead of once per proposal. Use 1 on memory-constrained machines. Checkpoints are still written sequentially by the coordinator after each completed proposal.
- **One restore per batch.** Proposals whose restore inputs are identical (the same restore files, test project and SDK image, compared by content digest) share one `dotnet restore` within a `bootstrap` batch, and the attempts of one `run` repair loop share it too. Later runs copy restore's `obj` output into their own fresh workspace and mount the downloaded package folder **read-only** into their build and test containers, so no run can change what another run builds against. A reused restore appears in `result.json` as a `restore` phase with `reused: true`, no network and no duration. Failed restores are never reused. The cache is deleted when the batch or run ends.
- `--debug` prints proposed C# test files to stderr, preserving JSON stdout. Generated code may contain confidential repository details; only enable it in trusted terminals or controlled logs. Debug output is grouped by target in concurrent batches.
- `bootstrap --format json` reports `timings.preparationSeconds`, `generationSeconds`, `verificationSeconds`, and `totalSeconds` to identify the real bottleneck. For an isolated comparison, use `bootstrap --all --generate-only` to skip containers. Each proposal still builds and tests in its own disposable workspace; only the read-only restore output is shared. Because the stages overlap, `generationSeconds` and `verificationSeconds` can add up to more than `totalSeconds`. No repository files are applied automatically.
- **Smart Resume**: All three generation batches (`bootstrap --all`, `generate --all-changed`, `journey-draft --plan-id`) save atomic external checkpoints **after every completed target**, including generated-but-not-yet-tested bootstrap proposals. Restart the same command with `--resume` and the same `--out`; intact outputs are reused without repeating successful LLM work. Failed/pending work is retried. Changed source code, project scope, ADO plan/links, UI snapshot or generation-relevant, non-secret model settings invalidate the relevant checkpoint. Changed input raises `bootstrap.changed` for Bootstrap or `resume.changed` for the other batches, instead of silently mixing runs. Checkpoint files: `bootstrap-checkpoint.json`, `generate-checkpoint.json`, `journey-draft-plan-<id>-checkpoint.json`. In-flight model requests cannot be resumed mid-token. Bootstrap never replaces an existing checkpoint by accident: a run without `--resume` stops with `bootstrap.checkpoint_exists`; pass `--restart` to discard it, or use a different `--out`.

## Terminal progress

The batch commands `bootstrap --all`, `generate --all-changed`, `journey-draft --plan-id`, and `apply --all` share one progress display. Interactive terminals show an updating Unicode bar with the current phase, completed/total counts, elapsed time, active workers, failures, and current target. Source discovery and ADO/browser preparation use indeterminate phases rather than misleading percentages. `--debug` still prints complete generated test source.

Redirected stderr, CI, and `TERM=dumb` produce plain, throttled milestone lines without ANSI cursor controls or colors. `NO_COLOR=1` or `CLICOLOR=0` disables colors while retaining interactive updates. `--format json` still writes exactly one JSON result to stdout; progress remains stderr-only. `bootstrap --progress-jsonl` keeps its existing versioned JSONL events on stderr instead of drawing a bar. Percentages represent completed work items, not estimated AI response progress. Rendering does not change checkpoint, retry, or exit-code behavior.

## Initial repository baseline (experimental)

```bash
testwizz bootstrap . --all --review              # discover, generate and run eligible C# methods
testwizz bootstrap . --all --review --max-workers 2 --verify-workers 2  # overlap independent work
testwizz bootstrap . --all --generate-only      # generate proposals without containers
testwizz bootstrap . --all --format json        # one aggregate JSON object on stdout
testwizz bootstrap . --all --review --max-workers 2 --resume  # resume the same scan
testwizz bootstrap . --all --generate-only --restart          # discard the checkpoint and start over
testwizz bootstrap . --all --generate-only --retries 0        # no retries for transient model failures
```

Bootstrap discovers public/internal/protected-internal C# methods with bodies, using overload-safe documentation IDs; tests and ignored files are excluded. **Generation uses a bounded, continuously replenished worker pool**; independent container executions can also overlap when explicitly configured, while checkpoint writes remain serialized. Progress is written to stderr (optionally as `--progress-jsonl`), and per-target generation/run outcomes are recorded. Proposals stay **outside** the repository and are **not applied automatically**. Generation requires a configured local model; running requires Docker/Podman.

**Failures are explained, not just counted.** Each failed target prints one sanitized stderr line with the phase, target, error code, message and proposal path, e.g. `generation failed for M:Shop.Forms.FormTypeExtensions.GetName(Shop.FormType): validation.missing-using: 'GetName' is an extension method ... · proposal <dir>`. The same reason is persisted in the checkpoint (`error` as `code: message`, plus `failure.code`/`failure.message`) and in the JSON result; `--progress-jsonl` events carry it as `error` instead of a text line. Messages are bounded, stripped of terminal control characters and redacted for tokens, keys, passwords, URL credentials and JWTs; stdout stays a single JSON document.

**Retries.** Transient model failures — timeouts, connection/DNS errors, empty or malformed responses and HTTP 408/425/429/5xx — are retried up to `--retries N` times per target (0-5, default 2) with exponential back-off starting at 2 s (capped at 30 s). Configuration, authorization, endpoint-policy, validation and cancellation failures are never retried, and every attempt goes through full validation, so a retry cannot turn an invalid proposal into an accepted one. Earlier attempts are kept in the checkpoint row's `attempts`. Cancellation (Ctrl+C) interrupts a back-off wait immediately; finished targets remain in the checkpoint for `--resume`.

**Progress counts** distinguish generation and execution. Proposals restored from the checkpoint count as valid generations from the start; the generation phase shows `valid (restored) · failed · remaining`, and the result has `generation` (`valid`, `generated`, `recovered`, `completed`, `failed`, `remaining`) and `execution` (`passed`, `failed`, `error`, `notRun`) objects. `--progress-jsonl` events include the same `generation` counters (and `execution` during verification).

**Bootstrap limitations:** C#/xUnit only; no Vue bootstrap, mid-request AI state recovery, or automatic application. `--resume` reuses verified completed targets and pending generated proposals from a matching checkpoint; failed executions are rerun without another model request. Target count is not a coverage guarantee; inferred expectations require review.


## Minimal UI test proposals (experimental)

A running **local** HTTP application, a Markdown file of acceptance criteria and an existing
project-local `@playwright/test` installation are required. No Git history, issue tracker,
external AI service or long-running autonomous agent is needed.

```bash
# In the target application's repository:
testwizz ui . --url http://localhost:5173/orders/new --criteria requirements/ORDER-42.md
```

Criteria are Markdown bullets (maximum 5 per invocation), for example:

```markdown
- Submitting without a delivery address shows a validation error.
- A valid delivery address allows submitting the order.
```

TestWizz captures one bounded accessibility snapshot of the local route with Playwright.
During inspection, only same-origin GET/HEAD browser requests are allowed; service workers, WebSockets and state-changing HTTP methods are blocked.
The configured local model receives the criterion statements plus the *observed* UI snapshot,
and writes one Playwright test per criterion. Each test retains its requirement evidence.
The generator checks structure, prohibited operations and JavaScript syntax without running
generated tests. It saves the familiar proposal bundle outside the working tree.

A snapshot recorded in advance can replace live browser inspection:

```bash
testwizz ui . --url http://localhost:5173/orders/new \
  --criteria requirements/ORDER-42.md --snapshot fixtures/checkout-snapshot.json
```

Snapshot JSON must contain a `snapshot` string. This path is useful for offline fixture tests.
Source criteria files are SHA-256 tracked; stale proposals cannot be applied. Generated files are
placed under `tests/e2e/`. **Review `plan.md`, `findings.md` and `changes.patch`.**
This command never executes generated browser tests. The existing `testwizz run` command
does not yet support UI proposals: it deliberately fails for unsupported stacks. Only an explicit
`testwizz apply --proposal <id> --allow-unverified` can copy an unexecuted UI proposal into
the repository, subject to the normal no-overwrite and staleness guards. Use the target
project's own Playwright runner after review; never point it at production.

Reproducible example (CI uses a deterministic **fake model**, not an AI quality benchmark):

```bash
dotnet build -c Release
npm install --prefix fixtures/ui --ignore-scripts --no-audit --no-fund
./fixtures/ui/node_modules/.bin/playwright install --with-deps chromium
node fixtures/ui/smoke.mjs
```

The test uses one local order form and two acceptance criteria. It verifies scenario/evidence
links, runs both generated Playwright tests against the correct app, and confirms that
deliberately breaking address validation fails exactly the corresponding test. It also checks
that malicious UI text cannot close the untrusted-data prompt block and that snapshot
inspection blocks WebSockets. The model response is fixed: this proves the **technical test
pipeline**, not a real model's selector accuracy or expected-result quality. Assess those
separately with the configured local model before trusting generated tests.

Limitations: only loopback HTTP URLs, one initial-page snapshot (no autonomous exploration,
authentication or multi-page traversal), up to five top-level Markdown bullets, no assertion
semantics verification, and no automatically verified UI run. No claims of end-to-end coverage
are made. Browser inspection executes the application's JavaScript locally; use only trusted
development/test applications and sanitized accounts/data.

## Configuration

`testwizz init` writes `testwizz.yaml` (the legacy `testwizz.json` is still supported). Equivalent YAML configuration:

```yaml
version: 1
ai:
  provider: ollama
  endpoint: http://127.0.0.1:11434
  model: qwen2.5-coder:14b
  apiKeyEnv: null
  timeoutSeconds: 120
  maxContextTokens: 16000
run:
  runtime: docker
  maxRepairAttempts: 2
output: ../.testwizz-proposals
```

Previously generated `testwizz.json` files remain valid without conversion. If both files exist, `testwizz.yaml` takes precedence. New repositories should use the common `<tool>.yaml` naming pattern alongside `gitwizz.yaml` and `docwizz.yaml`.

- `ai.provider`: `ollama` or `openai-compatible` (LM Studio, llama.cpp server, vLLM, ...).
- `ai.endpoint`: for Ollama the server root (`http://127.0.0.1:11434`); for OpenAI-compatible servers the base URL including the version, e.g. `http://127.0.0.1:1234/v1`.
- `ai.apiKeyEnv`: the *name* of an environment variable holding the key, never the key itself. `.env` files are not loaded.
  - For ordinary local Ollama, leave it `null` (no key required). For an authenticated local server, set it to e.g. `"TESTWIZZ_AI_API_KEY"`, then run `read -rsp 'Local AI API key: ' TESTWIZZ_AI_API_KEY; echo; export TESTWIZZ_AI_API_KEY` in Bash before `testwizz ai check .`.
  - See [Local AI providers and API keys](docs/ai-providers.md) for both configurations and security guidance.
- `run.runtime`: `docker` or `podman`. Generated tests only ever run inside a container.
- `run.image` (optional): SDK image for restore, build and test, e.g. one that trusts your organization's approved CA. Omit it to use `mcr.microsoft.com/dotnet/sdk:<version>` from the target framework; `run --image` takes precedence. See [Corporate environments](docs/corporate-environments.md).
- `output`: where proposals are written, relative to the config file. It defaults to a directory outside the repository.

Omitted keys use the defaults above. Unknown keys and invalid values are errors.

## Local model

```bash
ollama pull qwen2.5-coder:14b
testwizz init
testwizz ai check
```

`ai check` runs four steps: `apiKey` (env var set, if configured), `endpoint` (host resolves only to private addresses), `model` (installed on the server) and `structuredOutput` (a tiny synthetic prompt answered with schema-valid JSON). Exit code `1` means the server or model failed a step; `2` means configuration problems such as a public endpoint or a missing API key variable.

Network rules for every model call:

- Only loopback, RFC 1918 (`10/8`, `172.16/12`, `192.168/16`), `100.64/10` (Tailscale and similar VPNs) and IPv6 unique-local addresses are allowed. Link-local addresses, including cloud metadata, are rejected.
- The host is resolved on every connection; if *any* address is public the connection is refused, and the socket connects to the address that was checked.
- System proxies (`HTTP(S)_PROXY`) are ignored, redirects are not followed, and TLS certificates are validated normally. There is no option to skip validation; trust a self-signed certificate in your OS store instead.

## Context

```bash
testwizz context . --for OrderService.PlaceOrderAsync
testwizz context . --for "Calculator.Percentage(decimal, int)" --format json
```

Targets can be `Type`, `Namespace.Type`, `Type.Method`, `Type.Method(paramTypes)` or a documentation id (`M:Shop.Calculator.Add(System.Int32,System.Int32)`). Overloads without parameter types are rejected with a list of candidates; unknown names list similar ones. A symbol declared more than once (partial types or methods) is one target. When different types share a name (`App.Jobs.BackgroundTask` and `App.Legacy.BackgroundTask`), each candidate is listed fully qualified with its file and line and the exact `--for` documentation id.

The built-in provider uses Roslyn on the repository's `.cs` files (`git ls-files`, so `.gitignore` is respected) without running MSBuild. Types from NuGet packages are reported as `unresolved-type` diagnostics rather than guessed. `relatedTests` are tests that mention the target by name: a relationship, **not** coverage. A DocWizz provider will produce the same contract.

## Plan

```bash
testwizz plan . --for Calculator.Divide          # deterministic, no network
testwizz plan . --for OrderService.PlaceOrderAsync --ai --format json
```

The planner builds an **evidence catalog** from the method (guards, thrown exceptions, comparisons, early returns, switch cases, dependency calls, cancellation tokens, XML docs, related tests) and derives scenarios from it: happy path, one exception scenario per guard, boundary pairs for every comparison against a constant (`percent < 0` → `-1` vs `0`), early-return and switch-case scenarios, and cancellation. Every scenario cites evidence ids.

- Expectations that come only from the current code are `needsReview: true`, because the code may be wrong. An exception documented with `<exception cref=...>` is treated as confirmed.
- Existing tests that look like they cover a scenario are listed in `possibleDuplicateOf` (name heuristics, not coverage).
- With `--ai`, the model sees the repository data inside a block declared as untrusted, answers with schema-constrained JSON, and every proposed scenario must cite existing evidence ids. Anything else is dropped and reported under `findings`. AI scenarios are always marked for review. The plan records the model, its digest and the prompt version.

## Changed-code generation (initial C# slice)

```bash
testwizz generate . --since origin/main --list-changed --format json
testwizz generate . --since origin/main --list-changed --format text
# Generate up to 20 proposals, one per changed method, with an aggregate result:
testwizz generate . --since origin/main --all-changed --min-risk 40 --format json
testwizz generate . --since origin/main --for Calculator.Divide --format json
```

`--since` uses `git diff <ref>...HEAD --unified=0` to identify changed C# methods. `--list-changed` reports targets without calling AI; `--min-risk 0..100` filters by a deterministic heuristic, **not** measured defect probability or coverage. `--all-changed` generates up to 20 proposals and returns a `testwizz.generate-batch/v1` report; any failed target sets exit code 1. When several methods changed, use `--all-changed` or choose one with `--for`. Vue changed-code generation and additional frameworks are not implemented. Proposals remain outside the repository; no PR comments are posted.

## Generate

```bash
testwizz generate . --for OrderService.PlaceOrderAsync
testwizz generate . --for Calculator.Divide --ai-plan --format json
testwizz generate . --for Calculator.Divide --out ~/proposals
```

1. **Conventions** are detected from the repository: the test project that references the target's project, its xUnit package, mocking library (NSubstitute, Moq, FakeItEasy), assertion library (FluentAssertions, Shouldly, or plain `Assert`), implicit/global usings, namespace, naming pattern of existing tests, and one existing test file as a style example. Packages are never added; missing ones become findings.
2. TestWizz picks the **file path, namespace and class name** (never reusing an existing file); the model only returns code and assumptions.
3. **Validation**, without building or running anything:
   - C# syntax. If the model returned the file with an extra layer of string escaping, it is decoded once only when that removes the stray backslashes and reduces syntax errors (recorded as the warning `escaped-source-repaired`); legitimate escapes in literals are never unescaped on their own. Remaining backslashes outside literals are a `malformed-source` error;
   - every test carries `[Trait("Scenario", "S<n>")]` with a string-literal id that exists in the plan (`unknown-scenario` names the valid ids; a non-literal id is `unverifiable-scenario-link`), and uncovered scenarios are reported. The prompt lists the valid ids and forbids inventing new ones;
   - **accessibility** relative to the test project: the prompt lists which members of the type under test tests may use, which setters are read-only for them, and which members are private, protected or (without `InternalsVisibleTo`) internal. Writes to inaccessible setters (`read-only-member`), uses of such members (`inaccessible-member`) and reflection or `[UnsafeAccessor]` tricks to reach them (`encapsulation-bypass`) are errors; a scenario that cannot be tested through accessible members should be reported by the model as untestable instead;
   - `Assert.*` calls are checked by name against the detected xUnit version's real API (xUnit v2 2.9.3 or v3): `Assert.DoesNotThrow`, `Assert.Equals`, NUnit/MSTest-style assertions and v3-only methods in a v2 project are `unsupported-assertion` errors with a suggested alternative. A project's own `Assert` helper is not checked. `run` still verifies by compiling;
   - generated **helper types**: fakes of repository or BCL interfaces are checked by Roslyn (`incomplete-fake`, "verified by compilation"); fakes of common package interfaces that are not compiled offline (`ILogger`, `ILoggerFactory`, `IServiceScope`, `IServiceScopeFactory`, `IOptions`, `IHttpClientFactory`, …) are checked against their known members; other unresolvable interfaces only give the warning `unverified-fake`. A fake passed where the code under test expects an interface it does not implement is `argument-type-mismatch`, and a repository type used without its namespace is `missing-using`;
   - calls to members or constructors of repository types that don't exist, have the wrong arguments or aren't accessible are errors. This includes calls inside `Assert.Throws` lambdas and mock setups or verifications: TestWizz type-checks against small stand-ins for the xUnit, NSubstitute and Moq APIs;
   - repository **extension methods** bind exactly as in the real build: reduced (`type.GetName()`) and static (`FormTypeExtensions.GetName(type)`) calls, overloads, generic inference, optional and named parameters and nullable or interface receivers. The test project's `<Using>` items (plain, `Static="true"` and `Alias`, including `Directory.Build.props` and `Remove`) are applied as global usings. A call that fails only because the extension's namespace is not imported is reported as `missing-using` with the `using` to add (instead of Roslyn's fallback, which for an enum receiver is an unrelated `System.Enum.GetName` overload error); an incompatible receiver is `receiver-mismatch`. Genuinely invalid calls remain errors;
   - test quality: skipped tests (`Skip =`, `Assert.Skip*`), assertions that can never fail (`Assert.True(true)`, `Assert.Equal(4, 4)`, `throw new NotImplementedException()`), tests without assertions and duplicate test names are errors; tests that only check for null are warnings. Mock verifications (`Received`, `Verify`, `MustHaveHappened`), `Should*` chains and local `Assert*`/`Verify*` helpers count as assertions;
   - paths: the proposed file must be a plain relative path inside the repository (no `..`, no symbolic link leading outside) and must not exist yet.
4. The **proposal bundle** (`testwizz.proposal/v1`) is written to `<output>/<id>/`, where `output` comes from the config (default `../.testwizz-proposals`) or `--out`. An output directory inside the repository, including through a symbolic link, is refused. The bundle contains:

   | File | Content |
   |------|---------|
   | `manifest.json` | id, target, git revision, context and plan hashes, source file hashes (for stale checks), model + digest, prompt template, conventions, tests and their scenarios, assumptions, findings |
   | `plan.md` | the scenario plan |
   | `files/<path>` | each proposed file at its repository path |
   | `changes.patch` | the new files as a patch; `git apply <bundle>/changes.patch` adds them |
   | `findings.md` | findings, tests → scenarios, scenarios to review, assumptions |

   The id is the target plus a hash of the manifest, so the same input gives the same bundle; an existing bundle is never replaced (a `-2` suffix is added).

Exit code `1` means the proposal has errors; the bundle is still written so you can see why. Exit code `2` is a configuration or output-directory problem.

## Run

```bash
testwizz run . --proposal shop-calculator-divide-5242ec6f86c4
testwizz run . --proposal ../.testwizz-proposals/shop-calculator-divide-5242ec6f86c4 --format json
```

`run` needs Docker or Podman (`run.runtime` in `testwizz.yaml` or legacy JSON). Without a working runtime it fails with exit code 2; it never falls back to running tests on the host.

1. A throw-away copy of the repository is made in the system temp folder (tracked and untracked-but-not-ignored files, without `bin/`, `obj/`, `.git/`, and without symbolic links that point outside the repository). The repository itself is never mounted.
2. **Restore** (`dotnet restore <test project>`, network on) sees only project and build files: `*.csproj`, `*.props`, `*.targets`, solutions, `global.json`, `nuget.config`, lock files. Restore runs in a cache directory of its own; when the restore inputs are unchanged, repair attempts (and the other proposals of a `bootstrap` batch) reuse it instead of restoring again. Its `obj` output is copied into each run's workspace and the package folder is mounted read-only into build and test.
3. Then the sources and the proposed test file are added, and **build** and **test** run with `--network none`. Code from the repository and from the model only ever runs offline.
4. Every container runs with all capabilities dropped, `no-new-privileges`, a read-only root file system, 2 CPUs, 4 GB memory, 1024 processes and an overall time limit (`--timeout`, default 600 s). Containers are removed on completion, timeout and Ctrl+C.
5. Only the proposal's test class runs (`dotnet test --filter FullyQualifiedName~<Namespace>.<Class>.`). The image is `--image`, else `run.image` from the configuration, else `mcr.microsoft.com/dotnet/sdk:<version>` from the test project's target framework. The same image is used for every phase, repair attempt and `bootstrap` verification. For an SDK image that trusts a corporate CA, see [Corporate environments](docs/corporate-environments.md).
6. Before anything is copied, the workspace paths are checked with the temporary folder's file-system semantics. Paths listed twice are copied once; distinct paths that would land on the same entry (on Windows/macOS `web/_supplyPartList.ts` and `web/_SupplyPartList.ts`, or a file next to a folder of the same name) fail the run with `run.path_collision` naming both paths. Nothing is overwritten.

Results (`testwizz.run/v1`) are written to `<bundle>/run/`: `result.json`, `restore.log`, `build.log`, `test.log`, `results.trx`. They include the status (`passed`, `failed`, `compile-error`, `timeout`, `restore-failed`), compile errors with file, line, code and whether they are in the proposed file, and each test's outcome, message, stack trace and scenarios. Source files that changed since `generate` and bundle files edited by hand are reported. When a restore fails for a recognizable reason, `result.json` has a `diagnosis` (`restore.tls_untrusted`, `restore.feed_auth`, `restore.feed_unreachable`, `restore.image_unavailable`, `restore.package_not_found`) with a fixed remedy text that never quotes the log. Exit codes: `0` passed, `1` failed, compile error or timeout, `2` restore failure or setup problem.

### Repair

When the tests don't compile or fail, `run` sends the compile errors or failures, the current test file and the code under test back to the model (prompt `xunit-repair/v1`) and runs again, at most `run.maxRepairAttempts` times (default 2; `--no-repair` turns it off).

- Only the proposed test file changes, inside the bundle. The previous version is kept under `attempts/<n>/`, and the manifest hashes and `changes.patch` are updated.
- A repair is **rejected** if any test was removed or renamed, changed its scenario links, became skipped, gained an assertion that cannot fail, has fewer assertions, or swapped an exact assertion (`Equal`, `Throws<T>`, `Received`, …) for a looser one (`True`, `ThrowsAny`, `NotNull`, …). It must also pass the same static checks as `generate`. The next attempt is told why.
- Accepted repairs that change an assertion's expected value are listed under "changed expectations" for review.
- The model may decline to change a test it believes exposes a bug in the code; it reports that as a suspected bug.
- No repair is attempted for compile errors in existing repository code.
- Tests that still fail after the last attempt stay in the proposal, labeled **"possible bug in the code or a wrong test"**.

Every run, repair (accepted or rejected, with reasons, model and prompt version) and apply is recorded in the manifest's `history` and summarized at the end of `findings.md`.

## Apply

```bash
testwizz apply . --proposal shop-calculator-divide-5242ec6f86c4
```

`apply` writes the proposal's files into the repository and prints them. It does not commit; review with `git status` and `git diff`. It refuses when:

- the proposal has validation errors;
- a source file it was generated from has changed (generate again);
- the last run did not pass, no run was recorded, or a bundle file was edited after the run (unless `--allow-unverified`);
- a file would be overwritten, or a path is not inside the test project or resolves outside the repository through a symbolic link.

Files are created with create-new semantics; if one fails, the ones already written are removed again. Production files are never modified.

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Build and test from source:

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet run --project src/TestWizz.Cli -- help
```

For **installation and updates**, including Git-derived `-dev` NuGet packages, see [Getting started](docs/getting-started.md#install-or-update-the-global-tool). Updating the source checkout does not update the globally installed tool.

Source projects live under `src/TestWizz.*`; fixtures and regression tests under `fixtures/` and `tests/`. Real container tests are opt-in locally with `TESTWIZZ_DOCKER_TESTS=1`.

## Vue SFC context (initial slice)

```sh
testwizz context . --for src/components/CartItem.vue --format json
```

Vue context uses Node.js and the target project's `@vue/compiler-sfc`/`typescript` dependencies. Bundled read-only helpers extract SFCs and `.ts` composables/stores into `testwizz.context/v1`. Statically detectable prop, emit and slot names are supported, while details such as defaults and dynamic slots may remain unavailable. Context extraction does not call the model or change the repository. **Vue/Vitest test generation and execution are not implemented.**

### Vue surface extraction

The Vue context helper now reports static prop names (`defineProps`), event names (`defineEmits`) and template `<slot>` outlet names as `members` with `props:`, `emits:` and `slots:` identifiers. These are syntax-derived names; prop defaults, validators, payload types and dynamic slots remain explicitly unavailable.
