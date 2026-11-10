# Reconciliation

PostgreSQL holds Kasa Sika's **view** of a transfer. MTN holds the **truth**. When the two might disagree,
Kasa Sika asks MTN. It never guesses.

## When the outcome is uncertain

| Situation | State recorded | Why not success/failure |
|---|---|---|
| MTN returned 202 Accepted | `SUBMITTED` | Accepted means "received", not "paid" |
| Submit call timed out / connection dropped / 5xx | `UNKNOWN` | MTN may have received it |
| Process crashed after saving `SUBMITTING` | `SUBMITTING` → `UNKNOWN` after 2 min (stale) | Same |
| MTN status is unrecognised | `UNKNOWN` | Never interpret unknown text as success |
| MTN says "not found" for an accepted transfer | stays, recorded as an anomaly | Could be propagation delay |
| Still `UNKNOWN` after 30 min | `RECONCILIATION_REQUIRED` | Needs an operator |

## Who asks MTN

Three triggers, one code path (`ProviderStatusSync.SyncAsync`), so they behave identically:

1. The user (or the app's poll after submission): `POST /api/v1/conversation/{id}/status`.
2. A provider callback: `/api/v1/callbacks/{provider}` (the payload is only a hint; the status is re-fetched).
3. The background `ReconciliationWorker`, every 15 s: `SUBMITTED`, `PENDING`, `UNKNOWN`,
   `RECONCILIATION_REQUIRED` and stale `SUBMITTING`.

## Resolution rules

- `SUCCESSFUL` → `SUCCESS` (MTN financial transaction id kept as the reference).
- `FAILED` → `FAILED` with MTN's reason code.
- `PENDING` → `PENDING`.
- `NotFound` for an **uncertain** submission, after `NotFoundGraceMinutes` (10) since submission → `FAILED`
  with reason `PROVIDER_HAS_NO_RECORD`. MTN never received it, so nothing moved.
- A failed status *check* (MTN down) changes nothing; it is retried next cycle.
- **An uncertain transfer is never resubmitted.** The same `X-Reference-Id` is only ever queried. If an
  operator decides to retry, it must be a new transfer with a new reference, after MTN confirms the first
  one does not exist.

Every automatic decision writes a `reconciliation_records` row (`OPEN`, `RESOLVED_BY_PROVIDER_STATUS`,
`RESOLVED_NO_PROVIDER_RECORD`, `ESCALATED_TO_OPERATOR`) and a `transaction_events` row.

## What the user hears

| State | Spoken (English) |
|---|---|
| SUBMITTED / PENDING | "Your transaction is being processed. I will tell you as soon as MTN confirms it." |
| UNKNOWN / RECONCILIATION_REQUIRED | "I cannot confirm the result of this transaction yet. I have not marked it as successful or failed. I will keep checking with MTN." |
| SUCCESS | "Your 50 Ghana cedis transfer to Ama Owusu was successful." |
| FAILED (after submission) | "MTN reported that your transfer to Ama Owusu did not go through. The money was not sent." |

If the answer arrives after the user left, a `notifications` row stores the sentence in their language,
and it is spoken the next time they open the app.

## Demonstrating it

Mock scenarios (per contact in `appsettings.Development.json`, or forced from Settings → Presenter tools):

- **Kojo Antwi – Timeout**: submit "times out" → UNKNOWN is spoken → a few seconds later the reconciler
  finds it SUCCESSFUL. This is the case where a naive system would send the money twice.
- **Esi Appiah – Unknown**: stays UNKNOWN, then escalates. It is never shown as failed or successful.
- **Kofi Boateng – DuplicateCallback**: MTN-style callback delivered twice. The second is logged in
  `audit_logs` as `CALLBACK_DUPLICATE` and changes nothing.
