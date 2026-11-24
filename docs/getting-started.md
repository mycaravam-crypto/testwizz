# Getting started

Run commands in the **target application's repository** unless a step says to use the TestWizz source checkout. Requirements: .NET 10 SDK and a running **local model** (Ollama or OpenAI-compatible). Docker/Podman is needed **only** to run generated C#/xUnit tests. Browser workflows require a trusted local application and its Playwright dependencies.

## Install or update the global tool

In the **TestWizz source checkout** (Git Bash), build a package:

```bash
git pull --ff-only
git fetch --tags
dotnet pack src/TestWizz.Cli -c Release -o ./artifacts

# Extract the latest package version, including the -dev suffix.
package=$(ls -t artifacts/TestWizz.*.nupkg | head -n 1)
version=$(basename "$package" | sed -e 's/^TestWizz\.//' -e 's/\.nupkg$//')
echo "TestWizz package: $version"
```

For an **existing** global installation:

```bash
dotnet tool update --global TestWizz --version "$version" --add-source ./artifacts
hash -r
testwizz --version
```

For a first installation, use `dotnet tool install --global TestWizz --version "$version" --add-source ./artifacts` instead. The explicit `--version` is required for Git-derived `-dev` packages; NuGet searches stable versions by default. If an artificially higher version blocks the update, uninstall it once, then install the exact version. `git pull` alone never changes the installed tool.

## Configure a target project

In the **application repository**:

```bash
testwizz init .       # only if neither testwizz.yaml nor testwizz.json exists
# Adjust testwizz.yaml: ai.endpoint, ai.model and output.
testwizz config .
testwizz ai check .
```

Legacy `testwizz.json` remains supported, but `testwizz.yaml` takes precedence. Do not use `init --force` without backing up existing settings. See [configuration](../README.md#configuration) and [local-model authentication](ai-providers.md).

## Generate and apply C# tests

```bash
testwizz bootstrap . --all --review --max-workers 2 --verify-workers 2
# If interrupted, use the same output path and execution mode:
testwizz bootstrap . --all --review --max-workers 2 --verify-workers 2 --resume
testwizz apply . --all --verified-only
git status --short
dotnet test -c Release
```

`bootstrap` generates C#/xUnit proposals and tests them in Docker/Podman; `--verified-only` copies only previously passing, unchanged proposals. Review added files before committing. Without a container, `bootstrap . --all --generate-only` makes **unverified** proposals instead. Use `bootstrap . --all --resume` to continue a scan with unchanged sources, configuration and scan mode. An existing checkpoint is never replaced silently: rerunning without `--resume` stops with `bootstrap.checkpoint_exists`; use `--restart` to start over or a different `--out`. Both generated proposals and test outcomes are checkpointed; pending tests are rerun without regenerating their proposals. For changed-code batches, use `testwizz generate . --since main --all-changed --resume` after an initial run. Proposals and checkpoints remain outside the repository. See [bootstrap](../README.md#initial-repository-baseline-experimental).

Behind a TLS-inspecting corporate proxy, restore inside the container may not trust your organization's CA (`restore.tls_untrusted`). `testwizz preflight .` shows which stage fails. Build an approved CA-enabled SDK image and set `run.image`; see [Corporate environments](corporate-environments.md). Standard installations need no image or certificate settings.

For performance tuning, `--max-workers` controls concurrent model calls; `--verify-workers` controls concurrent isolated container tests (1-4, default 1). Increase them separately and compare `timings.generationSeconds` against `timings.verificationSeconds` in `--format json` output. Concurrent builds need enough CPU, RAM, and disk I/O. Generation and verification overlap, so the two timings can add up to more than `timings.totalSeconds`. Each proposal still builds and runs independently inside disposable containers; proposals with identical restore inputs share one restore, mounted read-only. If `docwizz.yaml` defines `scope.projects.include`, bootstrap limits C# loading to the listed .NET projects; invalid scopes are rejected.

## Draft an ADO test plan

With `ado auth status` succeeding and the application running locally:

```bash
export TESTWIZZ_JOURNEY_URL=http://127.0.0.1:5173/orders
export TESTWIZZ_STORAGE_STATE=.auth/docwizz-state.json  # if login is required
testwizz journey-draft . --plan-id 12 --list-only
testwizz journey-draft . --plan-id 12
# Restart a matching interrupted run:
testwizz journey-draft . --plan-id 12 --resume
```

Only explicit ADO Tested By links are used. Unlinked cases are skipped, drafts are not automatically executed, and all cases use the configured starting URL. To reuse a DocWizz login, copy its existing Playwright state into `.auth/` in the application repository, add `.auth/` to `.gitignore`, and use the **exact same origin** (hostname and port) as the login recording. Continue with [ADO journeys: review → capture → DocWizz](ado-journeys.md).

## Develop TestWizz itself

In the **TestWizz source checkout**:

```bash
dotnet restore
npm install --prefix fixtures/vue
dotnet build -c Release
dotnet test -c Release
dotnet run --project src/TestWizz.Cli -- help
```

Node.js 22 is required for the Vue fixtures. For the complete feature boundaries see the [README](../README.md) and [PLAN](../PLAN.md).
