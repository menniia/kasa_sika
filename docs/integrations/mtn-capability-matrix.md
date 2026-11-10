# MTN capability matrix

What is real today, what is simulated, and what still needs MTN. Use this table verbatim in Q&A.

Legend: **IMPLEMENTED** = code exists and is tested · **MOCK** = runs against the simulated provider ·
**SANDBOX** = coded against the verified MTN sandbox contract · **VERIFIED** = contract read from MTN's
official API definitions · **REQUIRES_MTN_CONFIRMATION** = needs MTN's answer or approval.

| Capability | Kasa Sika status | MTN API | Notes |
|---|---|---|---|
| Access token | IMPLEMENTED · SANDBOX · VERIFIED | `CreateAccessToken` | Cached, refreshed on 401 |
| Recipient is an active MoMo account | IMPLEMENTED · SANDBOX · VERIFIED | `ValidateAccountHolderStatus` | Before confirmation; failure → nothing sent |
| Transfer to a payee | IMPLEMENTED · SANDBOX · VERIFIED | `Transfer` | From **Kasa Sika's own** disbursement account, not the user's wallet |
| Transfer idempotency | IMPLEMENTED · VERIFIED | `X-Reference-Id` (409 on reuse) | Reference = transaction id, fixed before the call |
| Transfer status | IMPLEMENTED · SANDBOX · VERIFIED | `GetTransferStatus` | SUCCESSFUL / FAILED / PENDING; unknown → UNKNOWN |
| Callbacks | IMPLEMENTED · SANDBOX | `X-Callback-Url` | Untrusted nudge; status re-fetched; duplicates harmless. MTN documents no callback signature. |
| Reconciliation of uncertain transfers | IMPLEMENTED · MOCK · SANDBOX | `GetTransferStatus` | Background worker; UNKNOWN never becomes FAILED without MTN's answer |
| Balance of Kasa Sika's disbursement account | IMPLEMENTED (operator) · VERIFIED | `GetAccountBalance` | Never shown as the user's balance |
| **User's own wallet balance** | NOT AVAILABLE — REQUIRES_MTN_CONFIRMATION | consent (`GetUserInfoWithConsent` / consent products) | The app says so honestly: "check it in the MTN MoMo menu" |
| **Debit the user's own wallet** | NOT IMPLEMENTED — REQUIRES_MTN_CONFIRMATION | `bc-authorize` + `CreateOauth2Token` (CIBA), consent products, or Collections `RequestToPay` | The state machine and `consents` table are ready for it |
| MTN PIN entry | NEVER handled by Kasa Sika | MTN's own approval prompt | By design |
| Refunds / reversal | State exists (REVERSED); API not wired | `Refund-V1/V2`, `GetRefundStatus` | Future |
| Production (live money) | DISABLED | — | The API refuses to start with a non-sandbox target |
| Mock provider (demo) | IMPLEMENTED · MOCK | — | Same pipeline; scenarios: Success, Pending, Failure, Timeout, Unknown, DuplicateCallback, RecipientInvalid |

## Environment labels shown to users

| Mode | Label in the app | Money |
|---|---|---|
| Mock | DEMO — MOCK PAYMENTS | None exists |
| MTN sandbox | MTN SANDBOX — TEST MONEY | Sandbox test funds (EUR), Kasa Sika's sandbox account |
| Live | Not available | — |
