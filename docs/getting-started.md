# Getting started with TestWizz and local AI

TestWizz uses a locally hosted AI model to propose reviewable **C# / xUnit** tests. Vue support currently extracts component context; Vue/Vitest generation is not yet available.

## 1. Prerequisites

Install Git, the .NET 10 SDK, and [Ollama](https://ollama.com). Install Node.js 22 for Vue fixture checks and Docker or Podman for sandboxed execution of generated tests.

Verify the required tools:

```bash
git --version
dotnet --version
ollama --version
docker --version  # or: podman --version
```

## 2. Clone and build

```bash
git clone https://github.com/mycaravam-crypto/testwizz.git
cd testwizz
# To try the changes in PR #36 before they are merged:
git switch chore/repository-quality-parity
dotnet restore
npm install --prefix fixtures/vue --no-audit --no-fund
dotnet build -c Release
dotnet test -c Release
```

## 3. Install the CLI and set up the `tw` Bash alias

From the repository root, package and install the tool:

```bash
dotnet pack src/TestWizz.Cli -c Release -o artifacts
dotnet tool install --global TestWizz --add-source ./artifacts
```

If an earlier TestWizz version is already installed, use `dotnet tool update --global TestWizz --add-source ./artifacts` instead.

Add a short alias to your Bash configuration (run this once):

```bash
grep -qxF "alias tw='testwizz'" ~/.bashrc || printf "\n# TestWizz CLI\nalias tw='testwizz'\n" >> ~/.bashrc
source ~/.bashrc
tw --version
tw help
```

The global .NET tools directory (`$HOME/.dotnet/tools`) must be on your `PATH`. If `testwizz: command not found`, add it once to `~/.bashrc`:

```bash
grep -qxF 'export PATH="$HOME/.dotnet/tools:$PATH"' ~/.bashrc || printf '\nexport PATH="$HOME/.dotnet/tools:$PATH"\n' >> ~/.bashrc
source ~/.bashrc
```

To run the **source checkout** without installing a global tool, use the full command from the repository root:

```bash
dotnet run --project src/TestWizz.Cli -- help
```

## 4. Start a local Ollama model

```bash
ollama pull qwen2.5-coder:14b
```

Ollama usually runs a background service after installation. If not, start `ollama serve` in a second terminal. For lower-memory systems, use `qwen2.5-coder:7b` and update the configured model accordingly.

## 5. Initialize configuration in your target project

Use a **separate C# repository**, not necessarily the TestWizz source repository:

```bash
cd /path/to/your-csharp-project
tw init .
```

In `testwizz.json`, check these values (retain the other generated settings):

```json
{
  "version": 1,
  "ai": {
    "provider": "ollama",
    "endpoint": "http://127.0.0.1:11434",
    "model": "qwen2.5-coder:14b"
  }
}
```

Check the effective configuration and the model connection:

```bash
tw config .
tw ai check .
```

## 6. Inspect a real method and generate a proposal

Replace `Calculator.Divide` with a method that exists in your project.

```bash
tw context . --for Calculator.Divide
tw plan . --for Calculator.Divide --ai
tw generate . --for Calculator.Divide --ai-plan
```

The generation command prints a proposal ID/path. The generated tests are stored outside the repository by default, and are **not applied automatically**.

## 7. Run and review the proposal

Make sure Docker or Podman is running. Replace the placeholder with the printed proposal ID:

```bash
tw run . --proposal YOUR_PROPOSAL_ID
```

Review the generated tests, assumptions, scenarios, and run results. If the run passed and the output is acceptable:

```bash
tw apply . --proposal YOUR_PROPOSAL_ID
git status
git diff
dotnet test
```

Review and commit changes yourself. Never apply proposed assertions without checking their expected behavior.

## 8. Check Vue extraction (optional)

From the **TestWizz repository root**:

```bash
npm install --prefix fixtures/vue --no-audit --no-fund
tw context fixtures/vue --for src/components/CartItem.vue --format json
```

This reports Vue component context and static event call sites. It **does not** generate Vue/Vitest tests or require a model.

## Troubleshooting

- `tw: command not found`: reload `~/.bashrc` and verify `$HOME/.dotnet/tools` is on `PATH`.
- `ai check` fails: confirm the model name in `testwizz.json`, that `ollama list` includes it, and that the Ollama service is running.
- `run` fails to start: confirm the container runtime is installed, running, and accessible to your user.
- Build/test failures in the TestWizz source checkout: inspect the latest PR checks before assuming your local installation is broken.

For full command details and safety constraints, see [README](../README.md).
