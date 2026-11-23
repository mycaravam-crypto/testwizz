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
testwizz bootstrap . --all --review
testwizz apply . --all --verified-only
git status --short
dotnet test -c Release
```

`bootstrap` generates C#/xUnit proposals and tests them in Docker/Podman; `--verified-only` copies only previously passing, unchanged proposals. Review added files before committing. Without a container, `bootstrap . --all --generate-only` makes **unverified** proposals instead. Use `bootstrap . --all --resume` to continue a scan with unchanged sources, configuration and scan mode. Proposals and checkpoints remain outside the repository. See [bootstrap](../README.md#initial-repository-baseline-experimental).

If `docwizz.yaml` defines `scope.projects.include`, bootstrap limits C# loading to the listed .NET projects; invalid scopes are rejected.

## Draft an ADO test plan

With `ado auth status` succeeding and the application running locally:

```bash
export TESTWIZZ_JOURNEY_URL=http://127.0.0.1:5173/orders
testwizz journey-draft . --plan-id 12 --list-only
testwizz journey-draft . --plan-id 12
```

Only explicit ADO Tested By links are used. Unlinked cases are skipped, drafts are not automatically executed, and all cases use the configured starting URL. Continue with [ADO journeys: review → capture → DocWizz](ado-journeys.md).

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
