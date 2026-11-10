# Kasa Sika — Current State Audit

Audit date: 2026-09-22. Scope: every tracked and untracked source file in the repository, plus live
verification of the Khaya AI and MTN MoMo developer portals.

## 1. Repository layout

```
kasa_sika/
├── backend/                 .NET 10 solution (KasaSika.slnx) — clean architecture
│   ├── src/KasaSika.Domain          entities, value objects, state machine      (committed)
│   ├── src/KasaSika.Application     services, ports, options                   (UNCOMMITTED, untracked)
│   ├── src/KasaSika.Infrastructure  empty (Class1.cs)
│   ├── src/KasaSika.Api             ASP.NET template (WeatherForecast)
│   └── tests/KasaSika.UnitTests     empty (UnitTest1.cs)
└── mobile/                  Expo SDK 57 / React Native 0.86 / expo-router / NativeWind
```

**Important discrepancy with the brief.** The brief describes a *Node.js + TypeScript* backend. The
repository's backend is **.NET 10 (C#)**. Comments in `ProviderStatusSync.cs` ("The old Node code turned
any failed check into RECONCILIATION_REQUIRED…") show the team deliberately ported an earlier Node backend
to .NET, and that Node code is no longer in the repository. The .NET design is careful and worth keeping.
**Decision: keep .NET.** Rewriting it in Node would throw away the most carefully-reasoned part of the
codebase (the transaction state machine and submission protocol). Every architectural requirement in the
brief (modular monolith, provider abstraction, PostgreSQL, migrations, schema validation, idempotency) is
met equally well in .NET. Zod's role (deterministic schema validation of structured intent) is filled by
typed records + explicit validators on the backend; the mobile app validates API responses.

## 2. What already works

| Area | Status | Notes |
|---|---|---|
| `Money` value object | Works | Integer minor units, `ToSpeech()` ("50 Ghana cedis"). No floating point. |
| `Msisdn` value object | Works | Ghana mobile parsing (`024…`, `+233…`), masking (`024XXX1234`). |
| Transaction state machine (domain) | Designed well, has compile bugs | Explicit allowed-transition table; SUBMITTING persisted *before* the provider call; uncertainty never guessed into success/failure; idempotent `ApplyProviderStatus`; authorization fingerprint re-checked at submission (transaction-bound authentication). |
| Transaction events | Works | Append-only per-transaction history with actor, reason, correlation id. |
| Payment provider port | Designed | `IPaymentProvider` with Accepted / Rejected / Uncertain classification; callbacks treated as untrusted nudges. |
| Step-up authentication port | Designed | `IStepUpAuthenticator` with Passcode and clearly-labelled DemoSimulated implementations. |
| Auth service | Designed | Register/login, PBKDF2 passcode, lockout, constant-time "no such user", revocable sessions. |
| TransactionService | Designed | Idempotent create (unique user+key, fingerprint to detect key reuse), daily limit, confirm → authenticate → authorize → submit, status refresh, cancel, callback handling. |
| Reconciliation | Designed | Background candidate scan, stale-SUBMITTING detection, NotFound grace period. |
| Mobile screen | Runs against a backend that no longer exists | One screen, device speech recognition (`expo-speech-recognition`), `expo-speech` TTS, a "demo voice request" fallback, a partial English/Twi string table. |

## 3. What is partially implemented / broken

The backend **does not compile** as committed + uncommitted. Concrete defects found:

| File | Defect |
|---|---|
| `Domain/KasaException.cs` | Factory is spelled `NotFund`; callers use `NotFound`. Property `kind` (lower-case). |
| `Domain/Identity/User.cs` | `User.Create` never assigns `Msisdn` — every user would be stored with an empty phone number. |
| `Domain/Identity/Session.cs` | Property `ExpiredAt`; `AuthService` queries `ExpiresAt`. Stray `using static …JSType`. |
| `Domain/Transactions/Transaction.cs` | Property `RequestFingerrint` (typo); service reads `RequestFingerprint`. Actor typo `"systemt:validator"`. |
| `Domain/Transactions/Transaction.cs` | `ProviderTransferStatus.Unrecognised`; service uses `Unrecognized`. |
| `Domain/Payments/PaymentRecords.cs` | `using System.Transactions;` imports a `Transaction` type that collides with the domain `Transaction`. |
| `Application/DependencyInjection.cs` | Contains `class Class1` — no DI registration exists. |
| `Infrastructure` | Empty. No `DbContext`, no EF configuration, no migrations, no Npgsql, no passcode hasher, no field protector, no token service, no payment providers. |
| `Api` | ASP.NET template. No controllers besides WeatherForecast, no JWT auth, no error mapping, no configuration. |
| `UnitTests` | One empty test. |
| Mobile `api.ts` | Targets the removed Node API (`:3000`, `userId: "demo-user"` in the body, no auth token). |
| Mobile `i18n.ts` | Twi strings are UI-only; spoken output always uses `language: "en-GH"`. Several Twi strings are literal/awkward. |
| Mobile `useVoiceIntent.ts` | Hard-codes `lang: "en-GH"`; on-device recognisers have no Twi model. `expo-speech-recognition` is a native module, so the app cannot run in Expo Go. |
| Mobile `index.tsx` | Voice "confirm" is announced ("Say confirm") but never listened for — confirmation is button-only. Raw state names (`READY_FOR_CONFIRMATION`) shown to users. Speaks "success" text that includes a stray `=`. |
| Mobile template leftovers | `explore.tsx`, `app-tabs.tsx`, animated splash, theme files from the Expo template. `metro.conig.js` is misspelled, so NativeWind's Metro config is **never loaded**. `tailwind.config.js` `content` globs do not match `src/`. |

## 4. What is missing (against the brief)

- PostgreSQL wiring, schema, migrations, Docker Compose.
- Khaya AI integration of any kind (ASR, translation, TTS).
- A conversation engine (two-way dialogue, clarification, ambiguity handling, repeat/help/cancel/go back).
- Twi intent understanding; Twi spoken responses.
- MTN provider adapter (`MtnMomoProvider`) and mock provider (`MockPaymentProvider`) with scenarios.
- Devices, conversations, idempotency records, notifications tables.
- Accessible transaction history, "what was my last transaction", daily total.
- Environment indicator backed by the server's real provider mode.
- Demo reset, presenter scenarios, eyes-free ("screen curtain") mode.
- All documentation the brief lists (README is empty).
- Repository hygiene: there is **no root `.gitignore`**; `backend/**/bin` and `backend/**/obj` build outputs are committed (hundreds of generated files, including a copied `Microsoft.AspNetCore.OpenApi.dll`).

## 5. What can be reused as-is (after bug fixes)

- `Money`, `Msisdn`, `TransactionEvent`, the transition table, `Transaction` aggregate methods.
- `IPaymentProvider`, `SubmissionResult`, `ProviderCallback`, `ProviderUnavailableException`.
- `IStepUpAuthenticator`, `IPasscodeHasher`, `IFieldProtector`, `ITokenService`.
- `AuthService`, `TransactionService`, `ProviderStatusSync`, `ReconciliationService` (logic intact).
- `TransactionMapper` spoken wording ("accepted is never spoken as sent").
- Mobile: dependency set (Expo 57, expo-speech, expo-router, NativeWind), dark high-contrast palette idea.

## 6. Verified external facts (live, 2026-09-22)

### Khaya AI (developer.khaya.ai — Azure API Management)
Verified from the portal's public data API and with live calls using the project's subscription:
- Auth header `Ocp-Apim-Subscription-Key` (query alternative `subscription-key`).
- Gateway `https://developer-api.khaya.ai` (alias `https://translation-api.ghananlp.org`), URL-segment versioning.
- ASR v3 `POST /asr/v3/transcribe?language=twi`, raw audio body (`audio/wav|mpeg|flac|ogg`) → `{"text": …}`.
- TTS v2 `POST /tts/v2/synthesize` JSON `{text, language, speaker_id?, format?}` → audio bytes.
- Translation v2 `POST /v2/translate` JSON `{in, lang:"eng-twi"}` → bare JSON string.
- Language codes are ISO 639-3 (`twi`, `eng`, `ewe`, `gaa`, `dag`…); legacy `tw`/`en` are rejected.
- The first copy of the primary key (26 characters) was truncated and rejected with 401. The re-supplied
  primary key and the secondary key (both 32 characters) are verified working (HTTP 200). Both live only in
  `dotnet user-secrets` for the API project, outside the repository.
- Tiers: Developer (free) = **100 calls/month, 10 requests/minute**. This makes TTS caching mandatory.
- Round trip test: Twi TTS of "Fa Ghana sidi aduonum kɔma Ama" → Khaya ASR returned
  "Fahana si de aduonum ma Ama." Currency words get garbled; number words and names survive. The parser
  must be token-based and forgiving.
- Machine translation of a confirmation prompt dropped "to stop" — financial prompts need curated templates.

Full detail: [docs/integrations/khaya.md](../integrations/khaya.md).

### MTN MoMo (momodeveloper.mtn.com — Azure API Management)
- The Disbursements operation list matches the brief exactly.
- `Transfer` is documented as "transfer an amount **from the own account** to a payee account". It moves
  money out of the API user's (Kasa Sika's) disbursement account. **It does not debit the end user's wallet.**
- Consumer-consent operations (`bc-authorize`, `CreateOauth2Token` with CIBA grant, `GetUserInfoWithConsent`)
  exist; a consent-based transfer that debits the user's own wallet is **REQUIRES_MTN_CONFIRMATION**.

Full detail: [docs/integrations/mtn.md](../integrations/mtn.md),
[docs/integrations/mtn-capability-matrix.md](../integrations/mtn-capability-matrix.md).

## 7. What blocks the prototype today

1. Backend does not compile and has no infrastructure or API → nothing runs end to end.
2. The mobile app points at a backend that does not exist.
3. No Twi path: on-device recognisers do not do Twi, and nothing calls Khaya.
4. No conversation: confirmation is by button only; no clarification or ambiguity handling.
5. PostgreSQL is not connected. (Local PostgreSQL 18 is installed and running on this machine, but its
   `postgres` password is not available to the tooling; Docker is not installed.)
6. Khaya free-tier quota is tiny (100 calls/month) if the `ks` subscription is on the Developer tier.

## 8. Environment on the development machine

- .NET SDK 10.0.400, Node 20.20.1, npm 10.8.2.
- PostgreSQL 18 Windows service running on 5432 (scram-sha-256, password unknown to tooling).
- No Docker.
