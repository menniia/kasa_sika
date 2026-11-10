# Judging criteria → what the prototype demonstrates

2026 MTN Ghana Tɛkyerɛma Pa Hackathon. Every row points at something a judge can see or hear live.

## Communication Support & Two-Way Interaction — 20

| Demonstrated by | Where |
|---|---|
| Speak a request; Kasa Sika answers aloud and shows the transcript ("You said · Khaya AI (Twi)") | Talk screen |
| Read-back before anything happens: amount, name **and last four digits** | `CONFIRM_SEND` |
| Clarifying questions instead of guesses: "How much do you want to send to Kwame?", "I found 2 people called Kofi… say one or two" | Say "Send money to Kwame" / "Send 20 cedis to Kofi" |
| Corrections mid-flow: "send 20 cedis instead", "no, to Kwame" | Confirmation stage |
| Controls at any time: yes, no, cancel, repeat, help, go back, start again | Voice or dock buttons |
| Questions: "What was my last transaction?", "How much did I send today?", "Check my balance" (honest answer) | Any stage; mid-flow it answers, then returns: "Now, back to your transfer." |
| Hands-free: after each question the microphone re-opens (with a rising tone) | Settings → Hands-free |
| Results arriving later are spoken on next launch | `notifications` |

## Ghanaian-Language Support — 10

| Demonstrated by | Where |
|---|---|
| Twi speech recognised by **Khaya AI ASR** (`twi`), shown as "Khaya AI (Twi)" | Talk screen, TWI |
| Every reply spoken in Twi by **Khaya TTS**: confirmation, verification, processing, result, errors, help | `ResponseCatalog` (`tw`) |
| Twi amounts spoken as Twi numerals: "Ghana sidi aduonum" | `TwiWords` |
| Natural code-switching understood: "Fa 50 cedis kɔma Ama", "Send aduonum cedis to Ama" | `IntentParser` tests |
| Robust to real recogniser output: "Fahana si de aduonum ma Ama." → GH₵50 to Ama | Unit test with the live Khaya output |
| Language chosen first on the landing screen; switchable at any time; remembered | Landing, header EN\|TWI |
| Built for more languages: Ewe, Ga, Dagbani configured (disabled) with their Khaya codes | `appsettings.json` → `Languages` |

## Accessibility & Inclusion — 20

| Demonstrated by | Where |
|---|---|
| **No-sight-required transfer**: screen curtain on, whole screen = talk button, everything spoken | Settings → Screen curtain |
| Earcons + haptics: rising tone = listening, falling = stopped, double beep = your action needed, chime = success | `earcons.ts` |
| Automatic end-of-speech detection: no need to find a stop button | `useRecorder` |
| Screen reader: every control labelled with a hint; cards read as one sentence; live regions for status | All screens |
| Status is never colour alone: icon + words (✓ Successful, ⏳ Processing, ? Status unknown, ✕ Failed) | `StatusBadge` |
| Large targets (≥ 64 px; talk button 132 px), high contrast, scalable text, speaking-speed control | Theme, Settings |
| Passcode keypad in the standard phone layout, announced as a count, never the digit; "never say it aloud" | Verification sheet |
| Type-instead for people who cannot speak | Dock |
| Plain-language errors ("I couldn't reach the payment service. I have not sent your transaction.") | Everywhere |

## Technical Fit / Prototype Functionality — 20

| Demonstrated by | Where |
|---|---|
| Real stack running live: Expo app → .NET API → PostgreSQL → Khaya → provider | README quick start |
| Explicit 19-state transaction state machine; 202 ≠ success; timeout ≠ failure | `TransactionState.cs` |
| Idempotency end to end (turns, transfers, MTN reference); concurrent double submit reaches the provider once | Integration test |
| Transaction-bound, single-use step-up challenges; audit of every attempt | `authentication_*` tables |
| MTN Disbursements adapter against the verified contract; sandbox provisioning; callbacks; reconciliation | `docs/integrations/mtn.md` |
| Mock provider with 7 scenarios through the same pipeline | Presenter tools |
| Inspectable PostgreSQL state (readable enums, events, audit) | `docs/architecture/system-architecture.md` |
| Tests: parser, state machine, Khaya, MTN, integration on real PostgreSQL | `backend/tests` |

## UX/UI / Usability / Local Fit — 10

| Demonstrated by | Where |
|---|---|
| "Your money. Your voice. Your control." · "Kasa means speak. Sika means money." | Landing |
| GH₵, Ghana cedis, pesewas, Ghanaian numbers (024…), MTN MoMo context, Twi first-class | Everywhere |
| Warm, high-contrast theme with a single kente stripe; one clear action per screen | Theme |
| Trust card after every transfer: what you asked, what you authorised, what MTN reports, now | `TransactionCard` |

## Adoption & Continued Use — 10

| Demonstrated by | Where |
|---|---|
| Everyday tasks, not a one-off: send, history, "last transaction", "today's total", results announced later | Talk, History |
| Personalisation: language, speaking speed, hands-free remembered on the server | Preferences |
| Frequent recipients remembered (only after a successful transfer) | `contacts.use_count` |
| A path to what users do daily (airtime, bills, merchants) on the same provider seam | Architecture doc |
| Works where literacy or sight is the barrier, and keeps the user in control, so they can come back alone | Demo story |

## Presentation Clarity & Q&A — 10

- [demo-script.md](demo-script.md): a 4-minute script with the "phone turned away" moment.
- [q-and-a.md](q-and-a.md): prepared answers, including what is real, mock, sandbox, and what needs MTN.
- [../integrations/mtn-capability-matrix.md](../integrations/mtn-capability-matrix.md): honesty in one table.
