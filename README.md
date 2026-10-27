# testwizz

Generate reviewable unit tests for **.NET (xUnit)** and **Vue 3 (Vitest)** applications with a **local AI model** (Ollama or any OpenAI-compatible server). Your code never leaves your network.

Status: early development. See [PLAN.md](PLAN.md) for the design and milestones.

## Commands (implemented so far)

| Command | Description |
|---------|-------------|
| `testwizz help [command]` | Show available commands or help for one command. |
| `testwizz init [path] [--force]` | Create a default `testwizz.json` in a directory. Refuses to overwrite without `--force`. |
| `testwizz config [path]` | Validate `testwizz.json` and show the effective configuration. |
| `testwizz ai check [path]` | Check that the configured model is reachable, private, installed and returns schema-valid JSON. |
| `testwizz context [path] --for <target>` | Show the `testwizz.context/v1` context for a C# type or method (signature, source excerpt, injected dependencies, thrown exceptions, related tests). |
| `testwizz plan [path] --for <method> [--ai]` | Plan evidence-linked test scenarios (`testwizz.plan/v1`); `--ai` lets the local model propose more. |
| `testwizz generate [path] --for <method> [--out <dir>] [--ai-plan]` or `testwizz generate [path] --since <ref> [--list-changed] [--for <method>]` | Plan, let the local model write xUnit tests, validate them and save a proposal bundle outside the repository. The repository is not changed. |
| `testwizz run [path] --proposal <id> [--no-repair] [--image <image>] [--timeout <s>]` | Build and run a proposal's tests in a Docker/Podman container with the network off, and let the model repair failing tests (bounded). Results go into the bundle. Never runs on the host. |
| `testwizz apply [path] --proposal <id> [--allow-unverified]` | Copy a proposal's new test files into the repository after stale and overwrite checks. Nothing is committed. |

Global options: `--format text|json`, `--help`, `--version`.

With `--format json`, stdout contains exactly one JSON object (`"schema": "testwizz.output/v1"`); human-readable diagnostics go to stderr.
Exit codes: `0` success, `1` validation failed, `2` usage, configuration or runtime error.

## Initial repository baseline (experimental)

```bash
testwizz bootstrap . --all --review              # discover, generate and run eligible C# methods
testwizz bootstrap . --all --generate-only      # generate proposals without containers
testwizz bootstrap . --all --format json        # one aggregate JSON object on stdout
```

The initial implementation discovers public/internal/protected-internal C# methods with bodies, using overload-safe documentation IDs; tests and ignored files are excluded. It processes all eligible targets sequentially, writes progress to stderr, and records per-target generation and run outcomes. Proposal files stay **outside** the repository and are **not applied automatically**. Generation requires a configured local model; running requires Docker/Podman.

**Current limitations:** C# / xUnit only; no Vue generation, persistent resume/checkpoints, or automatic application. The number of eligible methods is not a guarantee of comprehensive test coverage; inferred expectations still require review.

## Configuration

`testwizz init` writes:

```json
{
  "version": 1,
  "ai": {
    "provider": "ollama",
    "endpoint": "http://127.0.0.1:11434",
    "model": "qwen2.5-coder:14b",
    "apiKeyEnv": null,
    "timeoutSeconds": 120,
    "maxContextTokens": 16000
  },
  "run": {
    "runtime": "docker",
    "maxRepairAttempts": 2
  },
  "output": "../.testwizz-proposals"
}
```

- `ai.provider`: `ollama` or `openai-compatible` (LM Studio, llama.cpp server, vLLM, ...).
- `ai.endpoint`: for Ollama the server root (`http://127.0.0.1:11434`); for OpenAI-compatible servers the base URL including the version, e.g. `http://127.0.0.1:1234/v1`.
- `ai.apiKeyEnv`: the *name* of an environment variable holding the key, never the key itself. `.env` files are not loaded.
  - For ordinary local Ollama, leave it `null` (no key required). For an authenticated local server, set it to e.g. `"TESTWIZZ_AI_API_KEY"`, then run `read -rsp 'Local AI API key: ' TESTWIZZ_AI_API_KEY; echo; export TESTWIZZ_AI_API_KEY` in Bash before `testwizz ai check .`.
  - See [Local AI providers and API keys](docs/ai-providers.md) for both configurations and security guidance.
- `run.runtime`: `docker` or `podman`. Generated tests only ever run inside a container.
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

Targets can be `Type`, `Namespace.Type`, `Type.Method`, `Type.Method(paramTypes)` or a documentation id (`M:Shop.Calculator.Add(System.Int32,System.Int32)`). Overloads without parameter types are rejected with a list of candidates; unknown names list similar ones.

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

`--since` uses `git diff <ref>...HEAD --unified=0` to identify methods whose new-side lines changed. With `--list-changed`, the sorted list of method targets is returned as a deterministic CI-friendly JSON object or Markdown table without contacting the AI endpoint. `--min-risk 0..100` filters methods using a deterministic heuristic for branching, throw guards, awaits, calls and changed lines. It is not a measured defect probability or test coverage. With `--all-changed`, TestWizz generates proposals for up to 20 changed C# methods and emits one batch status report (`testwizz.generate-batch/v1`) with each proposal path or error code; any failed target sets exit code 1. One changed method can be selected automatically; when multiple methods changed, use `--for` to choose one. The full multi-target batch/CI summary, Vue changed-code support and framework adapters remain future work under [#12](https://github.com/mycaravam-crypto/testwizz/issues/12). This command still generates proposals outside the repo and does not post PR comments.

## Generate

```bash
testwizz generate . --for OrderService.PlaceOrderAsync
testwizz generate . --for Calculator.Divide --ai-plan --format json
testwizz generate . --for Calculator.Divide --out ~/proposals
```

1. **Conventions** are detected from the repository: the test project that references the target's project, its xUnit package, mocking library (NSubstitute, Moq, FakeItEasy), assertion library (FluentAssertions, Shouldly, or plain `Assert`), implicit/global usings, namespace, naming pattern of existing tests, and one existing test file as a style example. Packages are never added; missing ones become findings.
2. TestWizz picks the **file path, namespace and class name** (never reusing an existing file); the model only returns code and assumptions.
3. **Validation**, without building or running anything:
   - C# syntax;
   - every test carries `[Trait("Scenario", "S<n>")]` for a scenario that exists in the plan, and uncovered scenarios are reported;
   - calls to members or constructors of repository types that don't exist, have the wrong arguments or aren't accessible are errors. This includes calls inside `Assert.Throws` lambdas and mock setups or verifications: TestWizz type-checks against small stand-ins for the xUnit, NSubstitute and Moq APIs;
   - test quality: skipped tests (`Skip =`, `Assert.Skip*`), assertions that can never fail (`Assert.True(true)`, `Assert.Equal(4, 4)`, `throw new NotImplementedException()`), tests without assertions and duplicate test names are errors; tests that only check for null are warnings. Mock verifications (`Received`, `Verify`, `MustHaveHappened`), `Should*` chains and local `Assert*`/`Verify*` helpers count as assertions;
   - paths: the proposed file must be a plain relative path inside the repository (no `..`, no symbolic link leading outside) and must not exist yet.
4. The **proposal bundle** (`testwizz.proposal/v1`) is written to `<output>/<id>/`, where `output` comes from `testwizz.json` (default `../.testwizz-proposals`, next to the repository) or `--out`. An output directory inside the repository, also via a symbolic link, is refused. The bundle contains:

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

`run` needs Docker or Podman (`run.runtime` in `testwizz.json`). Without a working runtime it fails with exit code 2; it never falls back to running tests on the host.

1. A throw-away copy of the repository is made in the system temp folder (tracked and untracked-but-not-ignored files, without `bin/`, `obj/`, `.git/`, and without symbolic links that point outside the repository). The repository itself is never mounted.
2. **Restore** (`dotnet restore <test project>`, network on) sees only project and build files: `*.csproj`, `*.props`, `*.targets`, solutions, `global.json`, `nuget.config`, lock files.
3. Then the sources and the proposed test file are added, and **build** and **test** run with `--network none`. Code from the repository and from the model only ever runs offline.
4. Every container runs with all capabilities dropped, `no-new-privileges`, a read-only root file system, 2 CPUs, 4 GB memory, 1024 processes and an overall time limit (`--timeout`, default 600 s). Containers are removed on completion, timeout and Ctrl+C.
5. Only the proposal's test class runs (`dotnet test --filter FullyQualifiedName~<Namespace>.<Class>.`). The image is `mcr.microsoft.com/dotnet/sdk:<version>` from the test project's target framework, or `--image` (for example an SDK image that trusts your corporate proxy's certificate).

Results (`testwizz.run/v1`) are written to `<bundle>/run/`: `result.json`, `restore.log`, `build.log`, `test.log`, `results.trx`. They include the status (`passed`, `failed`, `compile-error`, `timeout`, `restore-failed`), compile errors with file, line, code and whether they are in the proposed file, and each test's outcome, message, stack trace and scenarios. Source files that changed since `generate` and bundle files edited by hand are reported. Exit codes: `0` passed, `1` failed, compile error or timeout, `2` restore failure or setup problem.

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

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet test
dotnet run --project src/TestWizz.Cli -- help

# install as a local tool
dotnet pack src/TestWizz.Cli -c Release -o artifacts
dotnet tool install --global TestWizz --add-source ./artifacts
testwizz --version
```

Layout: `src/TestWizz.Cli` (commands, output), `src/TestWizz.Core` (configuration, contracts, proposal bundle and stack-neutral checks), `src/TestWizz.AI` (model clients, endpoint guard, `ai check`), `src/TestWizz.DotNet` (Roslyn context provider, C# scenario extractor, xUnit conventions/generator/validator), `fixtures/dotnet` (sample solution used by golden tests; regenerate goldens with `TESTWIZZ_UPDATE_GOLDEN=1 dotnet test`), `tests/TestWizz.UnitTests` (xUnit v3; model servers are faked on loopback, so no real AI or network is needed). Tests that start real containers run only with `TESTWIZZ_DOCKER_TESTS=1` (set in CI); `TESTWIZZ_DOCKER_IMAGE` overrides the SDK image they use.

## Vue SFC context (initial slice)

```sh
testwizz context . --for src/components/CartItem.vue --format json
```

Uses Node.js, `@vue/compiler-sfc` and `typescript` **from the target project's node_modules**; install its dependencies first. The bundled read-only Node helper parses the component and reports script bindings into `testwizz.context/v1`. The first slice does not yet extract full props/emits or composables/stores; those fields are marked unavailable. No model calls or repository modifications.

### Vue surface extraction

The Vue context helper now reports static prop names (`defineProps`), event names (`defineEmits`) and template `<slot>` outlet names as `members` with `props:`, `emits:` and `slots:` identifiers. These are syntax-derived names; prop defaults, validators, payload types and dynamic slots remain explicitly unavailable.
