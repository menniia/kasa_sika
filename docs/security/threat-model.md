# Kasa Sika threat model

Scope: the prototype in this repository (Expo app, .NET API, PostgreSQL, Khaya and MTN adapters).
The system's promise: **nobody else has to operate your phone or know your credentials for you to use
mobile money, and nothing is ever sent that you did not confirm and authenticate for.**

## Assets

1. The user's money (held by MTN; Kasa Sika can only *request* transfers).
2. The Kasa Sika passcode and the device key (step-up credentials).
3. Session tokens.
4. Personal data: phone numbers, contact names, transcripts, transaction history.
5. Server secrets: MTN subscription key / API user / API key, Khaya keys, JWT, encryption and fingerprint keys.

## Trust boundaries

```
 phone app ──TLS──▶ Kasa Sika API ──▶ PostgreSQL (app state only)
                         ├──────────▶ Khaya (text/audio only: no secrets, no PIN)
                         └──────────▶ MTN (all money movement; system of record)
```

The phone holds **no** provider credentials. Khaya receives only what the user said (audio/text) and the
text Kasa Sika speaks. MTN receives only what is needed to move money.

## Threats and mitigations

| Threat | Mitigation in this prototype | Residual risk / production work |
|---|---|---|
| **Stolen phone** | Every transfer needs step-up (passcode or biometric-released device key) *after* a spoken confirmation; sessions expire (8 h) and are revocable server-side (`sessions.revoked_at`, checked on every request); per-transfer (GH₵1,000) and daily (GH₵5,000) limits; 3 wrong passcodes → 15-minute lock. | Remote "lost phone" revocation UI; SIM-bound device attestation. |
| **SIM swap** | Kasa Sika identity is not the SIM: login needs the passcode; the biometric device key stays on the old phone; a new device must re-enrol with the passcode. | Registration does not yet verify SIM ownership (no OTP) — marked PROTOTYPE on `User.Msisdn`. Production: MTN SIM-swap check API before enrolment and high-value transfers. |
| **Account takeover (stolen token)** | Short-lived JWT bound to a server-side session; a token cannot authenticate a transfer (step-up is separate); device-key enrolment requires the passcode. | Refresh tokens and anomaly detection. |
| **Replay of a step-up proof** | Challenges are single-use (`consumed_at`), expire in 5 minutes (long enough for a user finding the keypad by touch), and are bound (keyed fingerprint) to user + transaction + amount + currency + recipient. The biometric signature covers the challenge id, nonce, transaction id, amount, currency and masked recipient. Tests: `A_challenge_for_one_transfer_cannot_authenticate_another_and_cannot_be_replayed`. | — |
| **Replay of a voice recording / voice cloning / synthetic voice** | Voice is **never** an authenticator. A recording or clone can at most *start* a request, which still needs confirmation and step-up on the device. See [voice-authentication-threat-model.md](voice-authentication-threat-model.md). | — |
| **Malicious audio** (hidden commands, TV in the background) | Same as above: speech builds a draft only. Everything is read back (amount, name, last 4 digits) and needs an explicit "yes" plus step-up. Audio size-limited (5 MB) and format-checked before reaching Khaya. | — |
| **Credential theft via speech** | The app says, every time: "Do not say your passcode out loud." Passcodes are typed on a keypad announced as a count ("3 of 6 digits entered"), never the digit. The passcode never goes through the conversation, ASR, translation or TTS, so it cannot reach Khaya or logs. | Shoulder surfing of the keypad: biometrics are preferred when enrolled. |
| **MoMo PIN exposure** | Kasa Sika never asks for, stores or transmits the MTN PIN. The passcode is explicitly "not your MoMo PIN". | When consent flows are added, the PIN is entered only in MTN's own prompt. |
| **LLM / prompt injection** | There is no LLM on the money path. Intent extraction is a deterministic parser producing typed facts (integer amount, contact from the user's own list, validated Msisdn). Queries ("last transaction") are fixed, owner-scoped SQL. Text can only select which fixed operation runs. | If an LLM is added, it must emit the same typed `ParsedUtterance` and go through the same validation. |
| **Transaction manipulation** (changing amount/recipient after confirmation) | The authorization fingerprint (keyed HMAC of user, transaction, amount, currency, recipient) is taken at authentication and re-checked at submission (`AUTHORIZATION_MISMATCH`). Corrections ("20 instead") cancel the draft and create a new one that must be confirmed again. | — |
| **Duplicate transfers** | Unique `(user_id, idempotency_key)`; conversation turns idempotent (`idempotency_keys`); SUBMITTING is persisted before calling MTN, under optimistic concurrency (`version`), so of N concurrent submits exactly one reaches MTN; MTN `X-Reference-Id` = transaction id (a retry cannot create a second transfer). Test: `Duplicate_submission_reaches_the_provider_exactly_once`. | — |
| **Callback forgery** | Callbacks carry no trusted data: only the reference is read, and the status is re-fetched from MTN over an authenticated call. A forged callback costs one status check. Rate-limited, 16 KB limit. | MTN documents no callback signature; if one becomes available, verify it. |
| **Callback replay / duplicates** | Unique fingerprint (`provider_callback_receipts`); a final transaction ignores later answers (`ApplyProviderStatus` is idempotent); one result notification per transfer (unique index). Test: `Duplicate_callback_is_recorded_once_and_changes_nothing`. | — |
| **Provider timeout / 5xx** | Recorded as UNKNOWN, never FAILED or SUCCESS; never resubmitted; the reconciler asks MTN about the same reference; "not found" becomes FAILED only after a grace period. Test: `A_timed_out_transfer_is_resolved_by_asking_the_provider_never_by_resubmitting`. | Operator dashboard for RECONCILIATION_REQUIRED. |
| **Database compromise** | Recipient phone numbers (transactions, contacts) are AES-256-GCM encrypted; passcodes are PBKDF2 (210k iterations); fingerprints are keyed HMAC (not brute-forceable offline); device keys encrypted; no balances stored anywhere. | Login MSISDN is stored in clear (needed for lookup); use a keyed blind index in production. Key management through a KMS. |
| **Leaked secrets** | No secrets in the repo, the app bundle or `EXPO_PUBLIC_*` vars. Secrets live in `dotnet user-secrets` / environment variables. The API refuses to start without keys, or with a live MTN target. `.gitignore` covers `.env*`, `appsettings.Local.json`. | Rotate keys that were ever pasted into chats or tickets (see "Operational notes"). |
| **Information disclosure between users** | Every query includes `user_id` from the token; another user's transaction is 404, not 403. Test: `Another_users_transaction_is_indistinguishable_from_one_that_does_not_exist`. | — |
| **Account enumeration / brute force** | Same response and same PBKDF2 work for unknown numbers and wrong passcodes; rate limit on auth endpoints (20/min/IP); lockout. | — |
| **Demo features in production** | Demo login, presenter controls and `DemoSimulated` step-up are refused at startup unless payments are mock or MTN sandbox. Demo reset touches only `is_demo` users. | — |
| **Misleading the user** | The environment label is always visible and comes from the server; mock/sandbox is also spoken at welcome. "Accepted" is never spoken as "sent"; uncertainty is never spoken as success or failure. Balance questions are answered honestly ("not available"). | — |

## Logging and retention

- Logs contain correlation ids, transaction ids and state names. They never contain passcodes, device
  keys, provider keys, full phone numbers or audio.
- `conversation_turns` stores transcripts (what was heard and said) for the visible history and audit.
  Production needs a retention policy (for example 30 days) and user deletion.
- Audio is not stored by Kasa Sika. It is sent to Khaya for recognition; Khaya's retention policy is
  REQUIRES_KHAYA_CONFIRMATION.

## Operational notes

- The Khaya keys for this project were shared in a chat transcript while this prototype was built.
  Treat them as exposed: regenerate both keys in the Khaya portal before any public demo or
  production use, and update the user-secrets.
