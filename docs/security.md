# Security model

TestWizz handles repository source as sensitive input to a local model. The endpoint guard restricts model traffic to private addresses. Review configuration and model-server logging separately before processing confidential repositories.

Generation writes proposals outside the repository. Container execution uses an offline build/test stage with restricted capabilities; package restoration may require network access and should use trusted package feeds. Never execute model-generated code outside the restricted runner.

CI performs dependency audits and CodeQL analysis. Security findings require triage; automated scanning and these controls are not an ISO/IEC 27001 certification.

## Quality gates

- Build and xUnit regression suite.
- Vue golden context fixtures and line mappings.
- High-severity npm dependency audit and NuGet vulnerability listing.
- Human review of model-generated assertions and any changed expectations.

Threats to revisit: prompt injection in source comments, dependency compromise, local model-server exposure, and runaway test workloads.
