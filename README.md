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
- `ai.endpoint`: for Ollama the server root (`http://127.0.0.1:11434`); for OpenAI-compatible servers the base URL including the version, e.g. `http://127.0.0.1:1234/v1`.
- `ai.apiKeyEnv`: the *name* of an environment variable holding the key, never the key itself. `.env` files are not loaded.
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

Layout: `src/TestWizz.Cli` (commands, output), `src/TestWizz.Core` (configuration and, later, contracts), `src/TestWizz.AI` (model clients, endpoint guard, `ai check`), `src/TestWizz.DotNet` (Roslyn context provider), `fixtures/dotnet` (sample solution used by golden tests; regenerate goldens with `TESTWIZZ_UPDATE_GOLDEN=1 dotnet test`), `tests/TestWizz.UnitTests` (xUnit v3; model servers are faked on loopback, so no real AI or network is needed).
