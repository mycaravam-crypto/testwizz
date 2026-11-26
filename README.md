# testwizz

Generate reviewable C#/xUnit test proposals with a local AI model, validate them and run them in isolated containers. TestWizz also extracts Vue 3 context, drafts Playwright UI tests and prepares reviewed Azure DevOps journeys for DocWizz manuals.

| Workflow | What you get |
|---|---|
| C#/xUnit | Evidence-linked scenarios, validated test proposals and isolated execution |
| Bootstrap | Repository baseline or changed-method proposals with resumable batches |
| Vue 3 | Component, composable and store context and scenario planning |
| Playwright | Review-first UI proposals from explicit acceptance criteria |
| Azure DevOps | Journey drafts and reviewed screenshot/assertion evidence |

Proposals remain outside the repository until explicitly applied. AI endpoints are restricted to private networks; use sanitized source, requirements and UI data. Vue/Vitest generation and execution of Vue/UI proposals by `testwizz run` are not implemented.

## Install

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Git. Generation needs Ollama or a private OpenAI-compatible model endpoint. Docker or Podman is required only for isolated C#/xUnit execution.

In the TestWizz source checkout (Bash):

```bash
git pull --ff-only
git fetch --tags
dotnet pack src/TestWizz.Cli -c Release -o ./artifacts
package=$(ls -t artifacts/TestWizz.*.nupkg | head -n 1)
version=$(basename "$package" | sed -e 's/^TestWizz\.//' -e 's/\.nupkg$//')
dotnet tool install --global TestWizz --version "$version" --add-source ./artifacts
testwizz --version
```

For an existing installation use `dotnet tool update` instead of `install`. See [installation and updates](docs/getting-started.md#install-or-update-the-global-tool).

## Quick start

In the target application repository:

```bash
testwizz init .                       # only if no configuration exists
# Configure the local model in testwizz.yaml
testwizz ai check .
testwizz preflight .                  # optional container and NuGet checks
testwizz bootstrap . --all --review --limit 10
testwizz apply . --all --verified-only
git status --short
```

Review generated expectations and applied changes before committing. Remove the limit for a full baseline; use a different output directory or explicitly restart when changing the checkpoint selection. Use `--generate-only` if containers are unavailable.

## Commands

| Command | Purpose |
|---|---|
| `init`, `config`, `ai check` | Configure and check the local model |
| `context`, `plan` | Inspect a target and derive evidence-linked scenarios |
| `generate` | Generate C#/xUnit proposals for a method or changed code |
| `bootstrap` | Generate and optionally verify a repository baseline |
| `preflight` | Check runtime, workspace, SDK image and NuGet connectivity |
| `run` | Verify a C#/xUnit proposal in isolation; optionally repair |
| `apply` | Apply reviewed proposals with freshness and overwrite guards |
| `ui` | Draft Playwright tests from explicit acceptance criteria |
| `journey-draft`, `journey` | Draft and capture reviewed ADO journeys |
| `help`, `--version` | Command help and version |

See [CLI reference](docs/cli.md) or `testwizz help <command>`.

## Configuration

`testwizz.yaml` takes precedence over legacy `testwizz.json`. Configure the AI provider, endpoint and model for your environment. API keys are referenced by environment-variable name, never stored in configuration. Custom SDK images and corporate CA trust are optional; TLS validation remains enabled.

See [configuration](docs/configuration.md), [AI providers](docs/ai-providers.md) and [corporate environments](docs/corporate-environments.md).

## Documentation

- [Getting started](docs/getting-started.md)
- [C# test generation, validation, run and apply](docs/test-generation.md)
- [Bootstrap, concurrency, checkpoints and metrics](docs/bootstrap.md)
- [UI proposals and Vue context](docs/ui-proposals.md)
- [Azure DevOps journeys and DocWizz](docs/ado-journeys.md)
- [Architecture](docs/architecture.md), [security](docs/security.md) and [test strategy](docs/testing.md)

Full reference: [docs/](docs/README.md). Roadmap: [PLAN.md](PLAN.md).

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Build and test from source:

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet run --project src/TestWizz.Cli -- help
```

Source projects live under `src/TestWizz.*`; fixtures and regression tests under `fixtures/` and `tests/`. Real container tests are opt-in locally with `TESTWIZZ_DOCKER_TESTS=1`.
