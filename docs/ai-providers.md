# Local AI providers

TestWizz supports Ollama and OpenAI-compatible local servers. Configure `ai.provider`, `ai.endpoint`, and `ai.model` in `testwizz.json`. Use `ai check` to validate connectivity, installed model, and schema-constrained response support.

The model endpoint must resolve only to permitted private addresses. System HTTP proxies and redirects are not used for model calls. The local-model check does not establish that generated tests are logically correct.

See [configuration in the README](../README.md#configuration).
