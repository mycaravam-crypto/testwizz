# testwizz

Generate reviewable unit tests for **.NET (xUnit)** and **Vue 3 (Vitest)** applications with a **local AI model** (Ollama or any OpenAI-compatible server). Your code never leaves your network.

Status: early development. See [PLAN.md](PLAN.md) for the design and milestones.

## Commands (implemented so far)

| Command | Description |
|---------|-------------|
| `testwizz help [command]` | Show available commands or help for one command. |
| `testwizz init [path] [--force]` | Create a default `testwizz.json` in a directory. Refuses to overwrite without `--force`. |
| `testwizz config [path]` | Validate `testwizz.json` and show the effective configuration. |

Global options: `--format text|json`, `--help`, `--version`.

With `--format json`, stdout contains exactly one JSON object (`"schema": "testwizz.output/v1"`); human-readable diagnostics go to stderr.
Exit codes: `0` success, `1` validation failed, `2` usage, configuration or runtime error.

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
- `ai.apiKeyEnv`: the *name* of an environment variable holding the key, never the key itself. `.env` files are not loaded.
- `run.runtime`: `docker` or `podman`. Generated tests only ever run inside a container.
- `output`: where proposals are written, relative to the config file. It defaults to a directory outside the repository.

Omitted keys use the defaults above. Unknown keys and invalid values are errors.

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

Layout: `src/TestWizz.Cli` (commands, output), `src/TestWizz.Core` (configuration and, later, contracts), `tests/TestWizz.UnitTests` (xUnit v3).
