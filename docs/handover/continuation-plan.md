# Kasa Sika — Continuation Plan

Companion to [current-state-audit.md](current-state-audit.md). Ordered by what judges will see, per the
brief's priority list. The application must stay runnable after each phase.

## Guiding decisions

1. **Keep the .NET backend.** Fix it, finish it, and do not port it to Node. (See the audit, section 1.)
2. **The conversation lives on the server.** The phone records audio or text and plays audio; the backend
   owns the dialogue state, the intent parser, the transaction state machine and every provider call.
   That keeps Khaya and MTN credentials off the phone and guarantees that no utterance can reach money
   movement except through confirm → authenticate → authorize → submit.
3. **No LLM on the money path.** Intent extraction is a deterministic, bilingual (English/Twi/code-switched),
   token-based parser producing a strict `ParsedIntent`. An `IIntentExtractor` seam allows an LLM to be
   added later for phrasing it cannot handle; its output would pass the same validation.
4. **Twi prompts are curated templates, not runtime machine translation.** Machine translation lost meaning
   in the live test. Templates are drafted with Khaya Translation, then reviewed; Khaya Translation stays as
   the fallback for text that has no template. Every Twi template is marked as requiring native-speaker
   review before judging.
5. **Khaya TTS output is cached on disk.** Free tier = 100 calls/month. Fixed prompts are pre-generated once.
6. **Authentication never uses speech.** A spoken secret is heard by bystanders and would be sent to the ASR
   provider. Step-up is a typed passcode on an accessible keypad, or device biometrics that release a
   device key (native), both bound to a single-use, transaction-specific challenge.
7. **Two payment modes, one pipeline.** `MockPaymentProvider` (deterministic scenarios) and `MtnMomoProvider`
   (sandbox) go through the same `TransactionService` and state machine. The UI always shows which.

## Phases

### Phase 0 — Repository hygiene
- Root `.gitignore`; untrack committed `bin/` and `obj/`.
- Secrets only in `dotnet user-secrets` (outside the repository) or environment variables.

### Phase 1 — Backend compiles and runs (judge-facing core flow)
- Fix domain defects listed in the audit.
- Expand the state machine to the brief's states: PARSED, VALIDATING, AUTHENTICATION_PENDING,
  AUTHORIZATION_PENDING, SUBMITTED, UNKNOWN, REVERSED (keeping every existing safety rule).
- Infrastructure: `KasaDbContext` (Npgsql, snake_case), EF migrations, PBKDF2 hasher, AES-GCM field
  protector, JWT issuer, reconciliation worker.
- API: JWT auth, versioned controllers, problem-details error mapping with normalized domain error codes.
- Seed a demo user and demo contacts (Ama, Kwame, Kofi Mensah, Kofi Boateng, Esi, Yaw).

### Phase 2 — Voice / two-way conversation
- `ConversationService` with stages (idle, awaiting amount, awaiting recipient, choosing between recipients,
  awaiting confirmation, awaiting authentication, processing, done) and commands (confirm, cancel, repeat,
  help, go back, start again, change amount, last transaction, today's total, balance).
- Ambiguity: two Kofis → ask which; missing amount → ask; missing recipient → ask; low-confidence/unparsed
  → ask to repeat. Never guess.
- Turn idempotency: each client turn carries a `turnId`; replays return the stored reply.

### Phase 3 — Twi / Khaya
- Ports: `ISpeechRecognitionProvider`, `ITranslationProvider`, `ITextToSpeechProvider`.
- Adapters: `KhayaSpeechRecognitionProvider`, `KhayaTranslationProvider`, `KhayaTextToSpeechProvider` over a
  single typed `KhayaClient`; `CachingTextToSpeechProvider` decorator; "unavailable" fallbacks.
- Language registry (`en`→`eng`, `tw`→`twi`; `ee`, `gaa`, `dag` configured but disabled).
- Twi number words, Twi command words, Twi response catalogue.

### Phase 4 — Accessibility (mobile)
- Voice-first home: one very large talk target; hands-free mode re-opens the microphone after each question.
- Earcons for "listening"/"stopped"; haptics on native.
- Screen-reader labels, hints, live regions; no state conveyed by colour alone; text scales.
- Eyes-free "screen curtain" mode for the no-sight demo.
- Typed input fallback for users who cannot speak.

### Phase 5 — Authentication
- Transaction-bound, single-use, expiring authentication challenge.
- Passcode keypad (all platforms); device-key + biometrics (native).
- `authentication_attempts` rows record user, device, transaction, amount, currency, recipient, method, result.

### Phase 6 — PostgreSQL state
- Tables: users, devices, sessions, authentication_attempts, authentication_challenges, device_credentials,
  consents, contacts, transactions, transaction_events, payment_provider_operations, idempotency_keys
  (conversation turns), conversations, audit_logs, notifications, reconciliation_records,
  provider_callback_receipts.
- Dev database without Docker: `scripts/dev-postgres.ps1` creates a private cluster on port 5433.
  Docker users: `docker compose up -d`.

### Phase 7 — MTN provider
- `MtnMomoProvider` (Disbursements): token cache, `ValidateAccountHolderStatus`, `Transfer`,
  `GetTransferStatus`, `GetAccountBalance`, callback parsing. Sandbox provisioning helper.
- `MockPaymentProvider` scenarios: SUCCESS, PENDING, FAILURE, TIMEOUT, UNKNOWN, DUPLICATE_CALLBACK,
  RECIPIENT_INVALID — per contact by default, overridable from the presenter panel.

### Phase 8 — Polished UX
- Landing ("Your money. Your voice. Your control."), English | Twi, "Try saying: Send 50 cedis to Ama".
- Conversation view with transcript, "What I heard" card, confirmation card, status timeline.
- Accessible history + detail ("what was requested / what was authorized / what the provider reports").

### Phase 9 — Demo scenarios & presenter panel
- Scenario picker, reset demo, live state readout.

### Phase 10 — Documentation
- README, demo script, judging criteria map, Q&A, architecture, reconciliation, Khaya, MTN, MTN capability
  matrix, threat models.

## Status after the first build session (2026-09-23)

| Phase | Status |
|---|---|
| 0 Hygiene | Done: root `.gitignore`; 400+ committed build files untracked |
| 1 Backend runs | Done: compiles clean; 19-state machine; migrations; JWT; error model; startup safety checks |
| 2 Conversation | Done and verified over HTTP: clarification, two-Kofi ambiguity, corrections, repeat, go back, start again, cancel, info queries |
| 3 Twi / Khaya | Done: ASR/TTS/translation adapters; Twi catalogue; verified with real Khaya output. **Twi wording needs native-speaker review** |
| 4 Accessibility | Built in the app (talk button, earcons, auto-stop, curtain, keypad, labels). **Not yet verified on a device or with TalkBack/VoiceOver** |
| 5 Authentication | Done: challenges bound to the transfer; passcode tested end to end; biometric path written, untested on a device |
| 6 PostgreSQL | Done: 18 tables; integration tests on real PostgreSQL |
| 7 MTN | Adapter done against the verified contract and unit-tested; **not yet run against the sandbox** (needs `Mtn:SubscriptionKey`) |
| 8–10 | UX, demo tools and docs written |
| Tests | 100/100 passing (unit + integration) |

### Next, in order

1. Run the app on a phone (Expo Go) and in Chrome; fix whatever real devices reveal (audio routing, the recorder on Android).
2. Test with TalkBack (Android) and VoiceOver (iOS).
3. Native Twi speaker review (checklist in `docs/integrations/khaya.md`), then re-run "Prepare Twi voice".
4. Put the MTN Disbursements key in user-secrets, provision the sandbox user, and run one sandbox transfer.
5. Rehearse `docs/hackathon/demo-script.md` twice, resetting the demo between runs.

## Deferred on purpose

- Speaker verification ("voice biometrics"). Without a liveness-capable provider it is security theatre
  against recordings and voice clones. The authenticator port allows it later; see
  [voice-authentication-threat-model.md](../security/voice-authentication-threat-model.md).
- Consent-based debit of the user's own wallet: REQUIRES_MTN_CONFIRMATION.
- Airtime, bills, merchant payments: architecture supports them; not built.
