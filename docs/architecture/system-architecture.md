# System architecture

## One sentence

A voice-first app talks to a Kasa Sika backend that understands the request (in English or Twi, via
Khaya AI), reads it back, authenticates the person for that exact transfer, and asks MTN to move the
money, then tracks MTN's answer until it is certain.

```
┌──────────────────────────────────────────┐
│           KASA SIKA APP (Expo)           │   voice first · screen reader · large targets
│  record 16 kHz WAV · play replies        │   holds NO provider credentials
│  passcode keypad · fingerprint           │
└────────────────────┬─────────────────────┘
                     │ HTTPS · JWT (session-bound, revocable)
┌────────────────────▼─────────────────────┐
│       KASA SIKA API (.NET 10, modular     │
│       monolith, clean architecture)       │
│                                           │
│  Conversation  → IntentParser (en/tw)     │   deterministic; no LLM on the money path
│       │          ResponseCatalog (en/tw)  │
│       ▼                                   │
│  TransactionService (state machine)       │   confirm → authenticate → authorize → submit
│  Step-up authenticators (passcode, device)│   transaction-bound challenges
│  ProviderStatusSync · Reconciliation      │   uncertainty resolved by asking MTN
│  Audit · notifications · idempotency      │
└───────┬───────────────┬───────────────┬──┘
        │               │               │
┌───────▼──────┐ ┌──────▼───────┐ ┌─────▼────────────┐
│ PostgreSQL   │ │ Khaya AI     │ │ Payment provider │
│ app state,   │ │ ASR v3 (twi) │ │  MtnMomoProvider │──▶ MTN MoMo APIs (sandbox)
│ audit, events│ │ TTS v2 (twi) │ │  MockPayment…    │    (same pipeline, no money)
│ NO balances  │ │ Translate v2 │ └──────────────────┘
└──────────────┘ └──────────────┘
```

## Projects

| Project | Responsibility | Depends on |
|---|---|---|
| `KasaSika.Domain` | Entities and rules: `Money` (integer minor units), `Msisdn`, `Transaction` + state machine, identity, challenges, conversations, contacts | nothing |
| `KasaSika.Application` | Use cases and ports: conversation, intent parsing, response catalogue, transactions, auth, speech, demo | Domain, EF Core abstractions |
| `KasaSika.Infrastructure` | Adapters: PostgreSQL (`KasaDbContext`, migrations), Khaya, MTN, mock provider, crypto, JWT, reconciliation worker | Application |
| `KasaSika.Api` | HTTP: controllers, JWT, error normalisation, rate limits, startup safety checks | all |
| `mobile/` | Expo SDK 57 app: landing, talk, history, details, settings | API over HTTP |

It is a modular monolith on purpose: one deployable, clear seams (ports), and no microservices before
there is a reason.

## The turn pipeline

```
audio (WAV) or text ──▶ SpeechService.TranscribeAsync ──▶ Khaya ASR (twi/eng)
                                     │ transcript (never invented on failure)
                                     ▼
                          IntentParser.Parse(text, user's contacts)
                                     │ facts: amount (minor units), phone, contact matches, commands
                                     ▼
                  ConversationService (stage-aware interpretation)
         missing amount? ask · missing recipient? ask · 2 Kofis? ask · unclear? ask to repeat
                                     │
                                     ▼
                  TransactionService.CreateAsync ──▶ provider: is recipient active?
                                     │
                                     ▼  READY_FOR_CONFIRMATION
             ResponseCatalog: "You are about to send 50 Ghana cedis to Ama Owusu,
                               number ending 0 1 1 1. Say yes to continue…" (en / tw)
                                     │
                                     ▼  SpeechService.Prepare: phone voice (en) or Khaya TTS clip (tw, cached)
```

## Transaction state machine

```
DRAFT → PARSED → VALIDATING → READY_FOR_CONFIRMATION → CONFIRMED → AUTHENTICATION_PENDING
      → AUTHENTICATED → AUTHORIZATION_PENDING → AUTHORIZED → SUBMITTING → SUBMITTED → PENDING
      → SUCCESS | FAILED | UNKNOWN (→ RECONCILIATION_REQUIRED)        SUCCESS → REVERSED
Any step before SUBMITTING → CANCELLED | EXPIRED
```

Rules enforced in `Transaction` / `TransactionStateMachine`:

- No transition skips a step. "The parser understood" cannot become "money sent".
- `SUBMITTING` is saved **before** MTN is called, under optimistic concurrency: one submission per transfer.
- 202 Accepted → `SUBMITTED`, not success. A timeout → `UNKNOWN`, not failed.
- Final states ignore later provider answers (duplicate callbacks are harmless).
- The authenticated fingerprint (user, transaction, amount, currency, recipient) is re-checked at submit.

## Data model (PostgreSQL)

`users`, `devices`, `device_credentials`, `sessions`, `authentication_attempts`,
`authentication_challenges`, `contacts`, `transactions`, `transaction_events` (append-only),
`consents`, `payment_provider_operations`, `provider_callback_receipts`, `reconciliation_records`,
`conversations`, `conversation_turns`, `idempotency_keys`, `notifications`, `audit_logs`.

- Money is `bigint amount_minor` + `char(3) currency`, with a CHECK `amount_minor > 0`. No floats, no balances.
- Enums are stored as text (`SUCCESS`), so the database is readable during the demo.
- Unique indexes carry the safety: `(user_id, idempotency_key)`, `(provider, provider_reference)`,
  callback `fingerprint`, `(user_id, scope, key)` for turns, one result notification per transfer.
- Recipient numbers are AES-GCM encrypted at rest.

Inspect a demo transfer:

```sql
select state, amount_minor, currency, recipient_name, recipient_masked, provider_reference, provider_financial_id
from transactions order by created_at desc limit 5;
select sequence, "from", "to", actor, reason from transaction_events where transaction_id = '<id>' order by sequence;
select method, result, amount_minor, recipient_masked, occurred_at from authentication_attempts order by occurred_at desc limit 5;
select operation, result, detail from audit_logs order by occurred_at desc limit 10;
```

## Speech routing

| Language | Recognition | Speaking |
|---|---|---|
| English | Phone recogniser if available (instant, free), else Khaya `eng` | Phone voice (`en-GH`) |
| Twi | Khaya ASR `twi` | Khaya TTS `twi` (cached MP3 clips, `/api/v1/speech/clips/{id}`) |
| Ewe, Ga, Dagbani | Configured, disabled | Add a catalogue column + enable in `Languages` config |

## Extension seams

- `IPaymentProvider`: other MoMo providers or banks; capability flags stop the conversation from claiming
  things a provider cannot do (e.g. the user's balance).
- `IStepUpAuthenticator`: passkeys, certified voice biometrics (with liveness), SIM-bound attestation.
- `ISpeechRecognitionProvider` / `ITextToSpeechProvider` / `ITranslationProvider`: more Ghanaian-language providers.
- An LLM intent extractor may be added **in front of** validation: it would emit the same `ParsedUtterance`.
