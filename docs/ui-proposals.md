# UI proposals and Vue context

## Minimal UI test proposals (experimental)

A running **local** HTTP application, a Markdown file of acceptance criteria and an existing
project-local `@playwright/test` installation are required. No Git history, issue tracker,
external AI service or long-running autonomous agent is needed.

```bash
# In the target application's repository:
testwizz ui . --url http://localhost:5173/orders/new --criteria requirements/ORDER-42.md
```

Criteria are Markdown bullets (maximum 5 per invocation), for example:

```markdown
- Submitting without a delivery address shows a validation error.
- A valid delivery address allows submitting the order.
```

TestWizz captures one bounded accessibility snapshot of the local route with Playwright.
During inspection, only same-origin GET/HEAD browser requests are allowed; service workers, WebSockets and state-changing HTTP methods are blocked.
The configured local model receives the criterion statements plus the *observed* UI snapshot,
and writes one Playwright test per criterion. Each test retains its requirement evidence.
The generator checks structure, prohibited operations and JavaScript syntax without running
generated tests. It saves the familiar proposal bundle outside the working tree.

A snapshot recorded in advance can replace live browser inspection:

```bash
testwizz ui . --url http://localhost:5173/orders/new \
  --criteria requirements/ORDER-42.md --snapshot fixtures/checkout-snapshot.json
```

Snapshot JSON must contain a `snapshot` string. This path is useful for offline fixture tests.
Source criteria files are SHA-256 tracked; stale proposals cannot be applied. Generated files are
placed under `tests/e2e/`. **Review `plan.md`, `findings.md` and `changes.patch`.**
This command never executes generated browser tests. The existing `testwizz run` command
does not yet support UI proposals: it deliberately fails for unsupported stacks. Only an explicit
`testwizz apply --proposal <id> --allow-unverified` can copy an unexecuted UI proposal into
the repository, subject to the normal no-overwrite and staleness guards. Use the target
project's own Playwright runner after review; never point it at production.

Reproducible example (CI uses a deterministic **fake model**, not an AI quality benchmark):

```bash
dotnet build -c Release
npm install --prefix fixtures/ui --ignore-scripts --no-audit --no-fund
./fixtures/ui/node_modules/.bin/playwright install --with-deps chromium
node fixtures/ui/smoke.mjs
```

The test uses one local order form and two acceptance criteria. It verifies scenario/evidence
links, runs both generated Playwright tests against the correct app, and confirms that
deliberately breaking address validation fails exactly the corresponding test. It also checks
that malicious UI text cannot close the untrusted-data prompt block and that snapshot
inspection blocks WebSockets. The model response is fixed: this proves the **technical test
pipeline**, not a real model's selector accuracy or expected-result quality. Assess those
separately with the configured local model before trusting generated tests.

Limitations: only loopback HTTP URLs, one initial-page snapshot (no autonomous exploration,
authentication or multi-page traversal), up to five top-level Markdown bullets, no assertion
semantics verification, and no automatically verified UI run. No claims of end-to-end coverage
are made. Browser inspection executes the application's JavaScript locally; use only trusted
development/test applications and sanitized accounts/data.

**What reaches the model from the page:** the accessibility snapshot (`ariaSnapshot`) of the initial page.
- It contains visible text, labels, and the *current values of form fields*.
- Values of password fields, and of fields marked `autocomplete` as credentials, one-time codes or card data, are cleared before the snapshot is taken. Their labels remain.
- JWTs are redacted.
- Hidden inputs, `display:none` and `aria-hidden` content and data attributes are not in the snapshot.
- Everything else visible, such as tokens shown as text or prefilled ordinary fields, is sent as is. Use test accounts with sanitized data. `fixtures/ui/snapshot-redaction.mjs` checks this boundary in a real browser.


## Vue SFC context (initial slice)

```sh
testwizz context . --for src/components/CartItem.vue --format json
```

Vue context uses Node.js and the target project's `@vue/compiler-sfc`/`typescript` dependencies. Bundled read-only helpers extract SFCs and `.ts` composables/stores into `testwizz.context/v1`. Statically detectable prop, emit and slot names are supported, while details such as defaults and dynamic slots may remain unavailable. Context extraction does not call the model or change the repository. **Vue/Vitest test generation and execution are not implemented.**

### Vue surface extraction

The Vue context helper now reports static prop names (`defineProps`), event names (`defineEmits`) and template `<slot>` outlet names as `members` with `props:`, `emits:` and `slots:` identifiers. These are syntax-derived names; prop defaults, validators, payload types and dynamic slots remain explicitly unavailable.

## Azure DevOps journeys and documentation

Start from an entire ADO test plan instead of entering individual work-item IDs. TestWizz reads **explicit Tested By links**, proposes browser steps with your **local model**, and saves review-required drafts:

```bash
# Run in the target application's repository with a trusted local app running.
ado auth status
testwizz ai check .
export TESTWIZZ_JOURNEY_URL=http://127.0.0.1:5173/orders
# If the application requires JWT: reuse the DocWizz Playwright login state.
export TESTWIZZ_STORAGE_STATE=.auth/docwizz-state.json
testwizz journey-draft . --plan-id 12 --list-only   # inspect links without AI/browser
testwizz journey-draft . --plan-id 12               # draft linked cases
testwizz journey-draft . --plan-id 12 --resume      # restart after interruption
```

A shared loopback start URL is required (environment variable or `--url`); ADO does not reliably provide a route per case. For protected pages, `--storage-state .auth/docwizz-state.json` (or `TESTWIZZ_STORAGE_STATE`) reuses the same Playwright cookies, localStorage and extended sessionStorage as DocWizz screenshots; the JWT stays out of the model prompt. Unlinked cases are skipped, multiple explicit links remain separate, and unsupported steps are reported. **Drafts are never executed or applied automatically.** Follow [ADO journeys](ado-journeys.md) to review, capture screenshot evidence, and publish a DocWizz manual.

For a single work item, `testwizz ui . --url http://127.0.0.1:5173/orders --ado-workitem 4711` creates review-first Playwright proposals; `testwizz run` cannot execute UI proposals. The original single-case `journey-draft` form remains supported (see `testwizz help journey-draft`).

**Data boundary:** ADO criteria, case steps and UI accessibility text are sent to the configured private model endpoint. Use sanitized data and trusted test applications.
