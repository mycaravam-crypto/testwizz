# Local AI providers and API keys

TestWizz supports **Ollama** and local **OpenAI-compatible** endpoints (such as LM Studio, llama.cpp, or vLLM). The API key is optional when your local server does not require authentication.

## Choose the protocol, not the model name

The `ai.provider` setting selects **different HTTP APIs**. A model such as `nvidia-nemotron-30b` might be hosted behind either API, so its name alone cannot determine the correct provider.

| | `ollama` | `openai-compatible` |
| --- | --- | --- |
| Typical server | Ollama native API | LM Studio, vLLM, llama.cpp, or an OpenAI API gateway |
| Configured endpoint | Server root, e.g. `http://127.0.0.1:11434` | API base, e.g. `http://127.0.0.1:1234/v1` |
| List models | `GET /api/tags` | `GET /models` |
| Generate | `POST /api/chat` | `POST /chat/completions` |
| Structured output | JSON schema in `format` | JSON schema in `response_format` |
| Authorization | Optional bearer token | Optional bearer token, depending on server |

TestWizz **appends** the API paths above to `ai.endpoint`. Do not include the full `/api/tags` or `/chat/completions` route in the configuration.

For a reverse proxy at `https://ai.example.internal`:

- If `https://ai.example.internal/api/tags` returns Ollama model JSON, set `provider: "ollama"` and `endpoint: "https://ai.example.internal"`.
- If `https://ai.example.internal/api/v1/models` returns OpenAI-style JSON with a `data` array, set `provider: "openai-compatible"` and `endpoint: "https://ai.example.internal/api/v1"`.
- A server may support both APIs. Choose the one whose **model listing and chat completion routes** work with that provider's response format.

For a protected server, use your existing exported key when probing:

```bash
curl -sS -i -H "Authorization: Bearer $TESTWIZZ_AI_API_KEY" https://ai.example.internal/api/tags
curl -sS -i -H "Authorization: Bearer $TESTWIZZ_AI_API_KEY" https://ai.example.internal/api/v1/models
```

Inspect the HTTP status, `Content-Type`, and response JSON structure without sharing credentials or sensitive model metadata. Do not use `curl -k` to bypass TLS verification.

A `Server response is not valid JSON` error during the `model` step usually means the configured endpoint returned a non-JSON response, such as a proxy login page or wrong-route error. It **does not** establish that the model is missing. Fix the API path or proxy authentication first, then run `tw ai check .`.

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
