# C# test generation and review

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
5. Only the proposal's test class runs (`dotnet test --filter FullyQualifiedName~<Namespace>.<Class>.`). The image is `--image`, else `run.image` from the configuration, else `mcr.microsoft.com/dotnet/sdk:<version>` from the test project's target framework. The same image is used for every phase, repair attempt and `bootstrap` verification. For an SDK image that trusts a corporate CA, see [Corporate environments](corporate-environments.md).
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


## Applying verified proposals in a batch

After generating and running proposals, use:

```bash
testwizz apply . --all --verified-only
testwizz apply . --all --allow-unverified
testwizz apply . --all --verified-only --out ../my-proposals --format json
```

The command processes proposal bundles in deterministic directory-name order. It applies only proposals with a successful last test run and unchanged generated files. Existing source-staleness checks and destination-file safeguards remain in effect. Invalid, unverified or stale proposals are **skipped**, while destination-path conflicts are reported as **conflicts**. Each proposal has an individual outcome and reason; the summary includes applied, skipped and conflicted counts plus the number of files added. Conflicts cause exit code `1`; skipped proposals alone do not. No Git commits are created. A second invocation will report already-applied files as conflicts (no overwrite).

`--all` requires exactly one of `--verified-only` and `--allow-unverified`; neither mode can be combined with `--proposal`. The unverified mode bypasses the successful-run requirement, but retains manifest validity, source freshness, path safety and no-overwrite checks. Use `testwizz run . --proposal <id>` to verify proposals before applying them.
