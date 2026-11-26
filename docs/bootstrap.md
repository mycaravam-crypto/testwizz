# Batch generation and bootstrap

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

**Controlled runs on large baselines.**

- `--limit <n>` processes only the first *n* discovered targets, in the deterministic discovery order (files in ordinal order, then declaration order). The JSON result reports `selection.discovered`, `selection.selected` and `selection.limit`. The selection is part of the checkpoint fingerprint, so `--resume` needs the same limit; the default is all targets.
- `--max-failure-rate <percent>` (opt-in, with `--failure-sample <n>`, default 10) stops *scheduling* new generations once more than that share of this run's finished generations failed. In-flight requests finish and are checkpointed, and the result reports `circuitBreaker.reason`. Unscheduled targets appear as `skipped` and are not checkpointed, so `--resume` continues with them.
- Every result has `metrics`:
  - `failuresByCode` for generation and execution (codes only, never messages);
  - `generationSuccessRate` and `validProposalsPerMinute`, which cover only generations finished in this run. Restored checkpoint results are excluded and counted as `restoredExcluded`. Throughput uses the generation wall-clock time, including time overlapping with verification.
  - The text summary shows the same figures.

**Retries.** Transient model failures — timeouts, connection/DNS errors, empty or malformed responses and HTTP 408/425/429/5xx — are retried up to `--retries N` times per target (0-5, default 2) with exponential back-off starting at 2 s (capped at 30 s). Configuration, authorization, endpoint-policy, validation and cancellation failures are never retried, and every attempt goes through full validation, so a retry cannot turn an invalid proposal into an accepted one. Earlier attempts are kept in the checkpoint row's `attempts`. Cancellation (Ctrl+C) interrupts a back-off wait immediately; finished targets remain in the checkpoint for `--resume`.

**Progress counts** distinguish generation and execution. Proposals restored from the checkpoint count as valid generations from the start; the generation phase shows `valid (restored) · failed · remaining`, and the result has `generation` (`valid`, `generated`, `recovered`, `completed`, `failed`, `remaining`) and `execution` (`passed`, `failed`, `error`, `notRun`) objects. `--progress-jsonl` events include the same `generation` counters (and `execution` during verification).

**Bootstrap limitations:** C#/xUnit only; no Vue bootstrap, mid-request AI state recovery, or automatic application. `--resume` reuses verified completed targets and pending generated proposals from a matching checkpoint; failed executions are rerun without another model request. Target count is not a coverage guarantee; inferred expectations require review.



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
