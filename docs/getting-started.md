# Getting started

Requirements: .NET 10 SDK, Node.js 22 for Vue fixtures, and Docker/Podman for running generated tests.

```sh
dotnet restore
npm ci --prefix fixtures/vue
dotnet build -c Release
dotnet test -c Release
dotnet run --project src/TestWizz.Cli -- help
```

To use a local model:

```sh
ollama pull qwen2.5-coder:14b
dotnet run --project src/TestWizz.Cli -- init .
dotnet run --project src/TestWizz.Cli -- ai check .
```

Adjust `testwizz.json` before `ai check` if your model differs. For detailed usage see [the README](../README.md).
