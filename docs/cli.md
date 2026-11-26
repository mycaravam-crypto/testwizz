# CLI reference

Run `testwizz help <command>` for command-specific options. See [getting started](getting-started.md) for installation.

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
| `testwizz preflight [path] [--image <image>] [--skip-network]` | Optional check before `run`/`bootstrap`, reported per stage: container runtime, workspace paths, whether the effective SDK image starts, and whether NuGet restores from inside it with TLS validation. Runs no repository code; credentials are redacted. See [Corporate environments](corporate-environments.md). |
| `testwizz apply [path] --proposal <id> [--allow-unverified]` | Copy a proposal's new test files into the repository after stale and overwrite checks. Nothing is committed. |
| `testwizz bootstrap [path] --all [--review \| --generate-only]` | Generate a repository baseline; see [batch options](bootstrap.md). |

Detailed workflows: [C# generation](test-generation.md), [bootstrap](bootstrap.md), [UI proposals](ui-proposals.md), [ADO journeys](ado-journeys.md).
