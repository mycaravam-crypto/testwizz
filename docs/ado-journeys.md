# ADO test plan → local AI → reviewed journey → DocWizz manual

This is a **review-first** workflow: TestWizz finds explicit requirement-to-test-case links in Azure DevOps (ADO), then proposes declarative browser steps using a private local model. It does **not** approve or execute proposals.

## Prerequisites

- Work in the **target application's repository**, not TestWizz's source checkout. Run a trusted application at an **HTTP loopback** URL.
- Install and authenticate the [ado-devops CLI](https://github.com/mycaravam-crypto/ado-devops) with work-item and test-plan read access. `ado auth status` must succeed; the ADO project comes from the repository or your configured project.
- Configure `testwizz.yaml` (or legacy JSON) and pass `testwizz ai check .`. Install the target application's Playwright dependencies and Chromium.
- Use disposable test accounts and synthetic data. ADO requirements, drafts, screenshots and model logs may be confidential.
- For a JWT-protected application, reuse DocWizz's `--storage-state` file. It must reside in the application repository, e.g. `.auth/docwizz-state.json`, and be excluded from Git. See [DocWizz screenshot authentication](https://github.com/mycaravam-crypto/docwizz/blob/main/docs/guides/screenshots.md#signed-in) for recording cookies/localStorage and the optional per-origin `sessionStorage` extension.

## 1. Inspect the plan's explicit links

Only the ADO **test plan ID** is needed:

```bash
export TESTWIZZ_JOURNEY_URL=http://127.0.0.1:5173/orders
export TESTWIZZ_STORAGE_STATE=.auth/docwizz-state.json  # only if JWT login required
testwizz journey-draft . --plan-id 12 --list-only --format json
```

TestWizz invokes read-only `ado testplan export` and `ado testplan trace-export`. Cases shared between suites are deduplicated; one case linked to several requirements yields several distinct pairs. Unlinked cases are reported as `skipped`, not guessed from titles.

`--list-only` performs **no model call or browser inspection**, so it does not read the storage-state file. It writes ADO export snapshots outside the repository. The trace defaults to the ADO project's root area; restrict it when necessary:

```bash
testwizz journey-draft . --plan-id 12 --area 'Shop\Orders' --list-only
```

Restricting the area may exclude relationships. Inspect the reported counts before generating drafts.

## 2. Generate all linked drafts

```bash
testwizz journey-draft . --plan-id 12 --out ../journey-drafts --format json
# On interruption, reuse the exact same output directory and plan:
testwizz journey-draft . --plan-id 12 --out ../journey-drafts --resume --format json
```

The URL comes from `TESTWIZZ_JOURNEY_URL` or an explicit `--url`. Authenticated capture additionally reads `TESTWIZZ_STORAGE_STATE` or `--storage-state`. The state format matches DocWizz's screenshot feature (cookies, localStorage, and its per-origin sessionStorage extension); tokens are **never copied to the KI prompt or output files**. If the session is expired or rejected, capture fails with `ui.auth_failed` rather than passing a login screen to the model. **All cases in a batch use the same starting route and a bounded, read-only UI accessibility snapshot.** ADO does not supply reliable routes per test case; verify each route during review.

Example external output:

```text
../journey-drafts/
  journey-draft-plan-12-checkpoint.json
  journey-plan-12-<run>/
    ado-plan.json
    ado-trace.json
  wi-4711-case-512-draft-<hash>/
    journey.json
    review.md
```

The versioned JSON result records every linked pair as `drafted`, `resumed`, `skipped` or `failed`, with paths or reason codes. Individual failures do not silently hide other cases. A checkpoint is updated atomically after each pair. `--resume` verifies file digests, unchanged ADO plan/trace data, the current UI snapshot and the current work-item revision/criteria before reusing an existing draft. A changed input fails closed (or regenerates the affected pair); no earlier model output is assumed current without validation. Valid drafts preserve the ADO step IDs and order, and propose one `click`/`fill` action plus one visibility assertion per step.

**Current limits:** 1–6 explicit steps per case; shared-step references are not expanded. Each linked requirement must expose 1–5 acceptance criteria. Interactive login and per-case route discovery are not automated. Reuse a session created by DocWizz instead. **The login-state origin must match the TestWizz URL exactly**: `localhost` and `127.0.0.1` are different. The read-only UI probe permits only same-origin GET/HEAD requests; unlike DocWizz's general screenshot capture, cross-origin API requests remain blocked by TestWizz. If your application calls an API on a second origin, this still needs a deliberately restricted network allowlist. The trusted application's JavaScript executes during the snapshot.

## 3. Review a draft

Compare `journey.json` and `review.md` to the original ADO test case. Check selectors, action order, expected results, test data, assumptions and current requirement revisions. A visibility assertion is **not proof** of business acceptance.

Copy the **approved** file into the application repository, for example `journeys/order-512.json`. The capture command requires a repository-relative reviewed steps file. Keep ADO exports and credentials out of Git.

## 4. Capture evidence from approved steps

Use the exported `ado-plan.json` path reported in the batch result:

```bash
testwizz journey . \
  --url http://127.0.0.1:5173/orders \
  --workitem 4711 \
  --plan ../journey-drafts/journey-plan-12-<run>/ado-plan.json \
  --case 512 \
  --steps journeys/order-512.json \
  --out ../journey-evidence
```

Replace the illustrative IDs and `<run>` with your actual output. This **separate** operation executes only reviewed declarative steps through a bounded interpreter; no AI-generated JavaScript is run. Actions can change data, so use a disposable local application.

## 5. Publish the DocWizz guide

```bash
docwizz generate . --html --journeys ../journey-evidence
```

DocWizz imports successful `testwizz.ui-evidence/v1` bundles and verifies screenshot hashes. The printable result is `docs/user-guides/manual.html`. Review screenshots and redact sensitive details before publication.

## Quality and security boundaries

- **Traceability:** Only explicit ADO links count; they indicate relationships, not passing acceptance tests.
- **Confidentiality:** The configured local model receives ADO criteria, test steps and sanitized UI snapshot text, **not** the storage-state file. Keep `.auth/` out of Git, restrict its permissions and revoke leaked sessions. Screenshots and other artifacts still require manual privacy review.
- **Integrity:** Model output remains review-required; only separately approved steps may be used for browser capture.
- **Availability:** Investigate skipped/failed items from the batch report; do not run state-changing journeys concurrently against shared test data.

The earlier single-case form (`testwizz journey-draft . --url ... --workitem ... --plan ... --case ...`) remains available. For Playwright test proposals rather than user guides, see [UI proposals](ui-proposals.md#minimal-ui-test-proposals-experimental).
