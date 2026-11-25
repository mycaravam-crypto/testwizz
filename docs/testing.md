# Test strategy

The existing `TestWizz.UnitTests` project contains unit tests, golden-context tests, fake-model contract tests, and opt-in real-container tests.

## Categories

- **Unit**: deterministic Roslyn, planning, configuration, validation, CLI parsing.
- **Integration**: model protocol against fake loopback servers, Vue/compiler-sfc fixture extraction, Docker/Podman behavior.
- **End-to-end**: generation -> proposal -> isolated run -> review -> apply, with dedicated local-model/manual validation.

The separate `IntegrationTests` and `E2ETests` projects hold smoke checks and future focused suites. Existing regression tests should be migrated incrementally, not duplicated or dropped.

Run `dotnet test` for fast checks. Container tests require `TESTWIZZ_DOCKER_TESTS=1`. Production source should never be mounted directly into an AI-generated test container.

## Evidence and quality

Use the Vue golden fixture for exact AST source mapping. Evaluate AI test proposals for compilation, meaningful assertions, scenario coverage, and repairs that weaken expectations. These are evidence of quality, not proof of complete code coverage.

## Concurrent model requests

`ConcurrentModelTests` pin down that one shared model client keeps requests isolated. It covers:

- answers arriving out of order;
- one request being cancelled or timing out while the others continue;
- mixed failures (HTTP 500/503, invalid JSON, schema mismatch), each reported for the request that failed with its own cause;
- concurrent digest lookups, which are coalesced into a single model listing.

A controlled bootstrap workload, with a deterministic fake model answering each request only from its own content, must produce byte-identical proposals at 1, 2 and 4 workers. The test also checks that 2 and 4 workers really overlap.

When a real model fails more often with several workers, compare the per-target failure codes (`ai.timeout`, `ai.http_error` with status, `ai.invalid_response`) at `--max-workers 1` and higher. Errors are not attributed to model degradation by TestWizz.

To reproduce real container runs behind a TLS-inspecting proxy, set `TESTWIZZ_DOCKER_IMAGE` to a CA-enabled SDK image (see [Corporate environments](corporate-environments.md)).
