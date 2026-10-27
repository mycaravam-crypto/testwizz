# Baseline quality benchmarks

The benchmark manifest describes expected static Vue source mappings. These fixtures are deterministic regression checks, not measured model quality or test coverage.

Run `dotnet test -c Release --filter FullyQualifiedName~Vue` after `npm install --prefix fixtures/vue` to validate Vue golden fixtures.

Future AI benchmarks should record model digest, deterministic prompts, pass rates, unreviewed assumptions, and cost/latency separately. Do not compare models without matching runtime configuration.
