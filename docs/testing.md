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
