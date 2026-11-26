# Configuration and local AI

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
  maxGenerationRepairs: 1
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
  - See [Local AI providers and API keys](ai-providers.md) for both configurations and security guidance.
- `ai.maxGenerationRepairs` (0-5, default 1): how often `generate` (and `bootstrap`/`--all-changed`) may send static validation errors back to the model for a corrected file. Each attempt is validated again and kept only if it has fewer errors and still covers every plan scenario the kept attempt covered; nothing is built or run for this. Attempts are listed in the manifest as `generationAttempts` when a repair happened. Invalid proposals are never executed by `bootstrap`.
- `run.runtime`: `docker` or `podman`. Generated tests only ever run inside a container.
- `run.image` (optional): SDK image for restore, build and test, e.g. one that trusts your organization's approved CA. Omit it to use `mcr.microsoft.com/dotnet/sdk:<version>` from the target framework; `run --image` takes precedence. See [Corporate environments](corporate-environments.md).
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
