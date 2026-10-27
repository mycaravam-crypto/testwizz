# Local AI providers and API keys

TestWizz supports **Ollama** and local **OpenAI-compatible** endpoints (such as LM Studio, llama.cpp, or vLLM). The API key is optional when your local server does not require authentication.

## Ollama without an API key

A typical local Ollama server does not require an API key. Set `ai.apiKeyEnv` to `null` (or omit it):

```json
{
  "version": 1,
  "ai": {
    "provider": "ollama",
    "endpoint": "http://127.0.0.1:11434",
    "model": "qwen2.5-coder:14b",
    "apiKeyEnv": null
  }
}
```

```bash
ollama pull qwen2.5-coder:14b
tw ai check .
```

If you have not installed the `tw` alias, use `testwizz` instead.

## Authenticated local OpenAI-compatible endpoint

If the local server **requires** a bearer token, configure the *environment variable name*, **not the secret itself**, in `testwizz.json`:

```json
{
  "version": 1,
  "ai": {
    "provider": "openai-compatible",
    "endpoint": "http://127.0.0.1:1234/v1",
    "model": "your-local-model-id",
    "apiKeyEnv": "TESTWIZZ_AI_API_KEY"
  }
}
```

Set the variable in your Bash session without recording the secret in your command history:

```bash
read -rsp 'Local AI API key: ' TESTWIZZ_AI_API_KEY
echo
export TESTWIZZ_AI_API_KEY
tw ai check .
```

Replace the endpoint and model ID with values from your server. TestWizz reads `TESTWIZZ_AI_API_KEY` from its process environment and sends it as `Authorization: Bearer <token>`. It rejects configuration with a named but unset/empty environment variable before connecting.

**Optional persistent setup:** store the key in an appropriate OS secret manager and load it into your shell at session start. Avoid putting the literal key in `~/.bashrc`, `testwizz.json`, tracked scripts, CI logs, or committed `.env` files. TestWizz does **not** automatically load `.env` files.

If the endpoint does not enforce authentication, keep `apiKeyEnv: null` instead of setting a dummy key. For an Ollama instance behind an authenticated local reverse proxy, the same `apiKeyEnv` mechanism can be used with `provider: "ollama"`.

## Check and troubleshoot

```bash
tw config .
tw ai check .
```

`ai check` validates the configured variable, private endpoint resolution, model availability, and a small structured-output response. Common failures:

- **Missing API key**: `apiKeyEnv` names a variable that is not exported into the running shell.
- **HTTP 401/403**: the server rejected the bearer token; check its server-side authentication settings.
- **Model not found**: use the exact model ID exposed by the server; for Ollama, check `ollama list`.
- **Connection refused**: start your local model server and verify the endpoint and port.
- **Public endpoint rejected**: TestWizz intentionally accepts only permitted private-network destinations.

These settings are for **TestWizz's local model**, not the separate `SMARTWIZZ_API_KEY` GitHub Actions secret used by SmartWizz review workflows.

See [the README](../README.md#configuration) for all configuration properties and network restrictions.
