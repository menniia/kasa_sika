# Khaya AI integration

Khaya AI is Kasa Sika's Ghanaian-language provider: it recognises Twi (and African English) speech,
speaks Twi replies, and translates text that has no curated template. It is **only** a language
provider. It never sees MTN credentials, passcodes or MoMo PINs, and it has no role in authentication,
authorization, transaction state or money movement.

Every fact below was verified on **2026-09-22** from the official developer portal
(https://developer.khaya.ai, an Azure API Management portal). The API definitions were read from the
portal's public data API (`/developer/apis?api-version=2022-04-01-preview`), and then confirmed with
live calls using the project's `ks` subscription. Anything not verified is marked
**REQUIRES_KHAYA_CONFIRMATION**.

## Subscription

| Item | Value |
|---|---|
| Subscription name | `ks` |
| State | Active (started 2026-09-22) |
| Keys | Primary and secondary, both verified working (HTTP 200) |
| Where the keys live | `dotnet user-secrets` for `backend/src/KasaSika.Api` (`Khaya:SubscriptionKey`, `Khaya:SecondarySubscriptionKey`), or environment variables `Khaya__SubscriptionKey` / `Khaya__SecondarySubscriptionKey`. **Never** in the repository, the mobile app, logs or the database. |
| Tier | REQUIRES_KHAYA_CONFIRMATION. Check the portal's Profile page. |

Tiers published on the portal (`/developer/products`):

| Tier | Quota | Rate limit |
|---|---|---|
| Developer (free) | 100 calls / month | 10 requests / minute |
| Basic ($14.95/month) | 3,000 calls / month | 50 / minute |
| Standard ($89.95/month) | 20,000 calls / month | 100 / minute |
| Enterprise ($749/month) | 200,000 calls / month | 3,500 / minute |

If `ks` is on the free Developer tier, 100 calls is about 30 full rehearsals. That is why every
synthesized reply is cached on disk (`backend/src/KasaSika.Api/.cache/tts`, git-ignored), and why English
speech uses the phone's own recogniser when one is available.

## Authentication

- Header: `Ocp-Apim-Subscription-Key: <key>` (the query-string alternative `subscription-key` exists; we
  do not use it, because query strings end up in logs).
- Gateway: `https://developer-api.khaya.ai` (alias `https://translation-api.ghananlp.org`).
- Versioning: URL segment (`/asr/v3`, `/tts/v2`, `/v2`).
- On `401` from the primary key, `KhayaClient` retries once with the secondary key, so keys can be rotated.

## APIs used

### Automatic Speech Recognition v3

| | |
|---|---|
| Endpoint | `POST /asr/v3/transcribe?language={code}` |
| Optional query | `timestamps=word\|segment` (not used) |
| Body | Raw audio bytes. `Content-Type` one of `audio/wav`, `audio/mpeg`, `audio/flac`, `audio/ogg` |
| Response 200 | `{"text": "..."}` (plus `timings` when requested) |
| Language list | `GET /asr/v3/languages` → `{"languages":[{"code":"twi","name":"Twi"}, ...]}` |
| Twi | `twi` (Twi) and `atw` (Akuapem Twi) |
| English | `eng` ("African English") |
| Streaming | Not offered by v3 |
| Confidence scores | Not returned. Kasa Sika never relies on confidence: everything is read back and confirmed. |

The app records 16 kHz mono 16-bit PCM WAV on every platform (expo-audio's PCM stream, encoded in the
app), because Android and iOS recorders produce AAC/3GP, which Khaya does not accept.

### Text-to-Speech v2

| | |
|---|---|
| Endpoint | `POST /tts/v2/synthesize` |
| Body | `{"text": "...", "language": "twi", "speaker_id": "female", "format": "mp3", "stream": false}` |
| Speakers | `GET /tts/v2/speakers` → `male_low`, `male_high`, `female` (shared across all languages) |
| Formats | `wav` (default), `mp3`, `ogg`. Streaming is available for wav/mp3 (`stream: true`); we do not use it. |
| Twi | `twi` (Asante Twi) and `atw` (Akuapem Twi) |
| Observed | The WAV output is 16 kHz, 32-bit float. We request **MP3**, because some Android players reject float WAV. |

### Translation v2

| | |
|---|---|
| Endpoint | `POST /v2/translate` |
| Body | `{"in": "text (max 1000 chars)", "lang": "eng-twi"}` |
| Response 200 | A bare JSON string: `"Wo ho te sɛn?"` |
| Languages | `GET /v2/languages` → eng, twi, ewe, gaa, fat, yor, dag, kik, gur, luo, mer, kus |

### Language codes

All three APIs use **ISO 639-3** codes. Legacy codes (`tw`, `en`) are rejected with
`UNSUPPORTED_LANGUAGE` / `UNSUPPORTED_LANGUAGE_CODE`. Kasa Sika keeps its own app codes (`en`, `tw`) and
maps them in configuration (`Languages:Supported[].ProviderCode`).

## Errors (documented, and mapped in `KhayaClient`)

| HTTP | Body | Kasa Sika `SpeechFailure` |
|---|---|---|
| 400 | `{"error":{"code":"VALIDATION_FAILED","details":[{"code":"EMPTY_AUDIO"\|"INVALID_AUDIO_FORMAT",...}]}}` | `InvalidAudio` → "I did not hear anything" |
| 400 | details code `UNSUPPORTED_LANGUAGE*` | `UnsupportedLanguage` |
| 400 | other details (`EMPTY_TEXT`, `INVALID_SPEAKER`, ...) | `InvalidInput` |
| 401 | `{"statusCode":401,"message":"Access denied due to invalid subscription key..."}` (gateway) | retry with the secondary key, then `NotAuthorized` |
| 403 | quota exhausted (gateway) — REQUIRES_KHAYA_CONFIRMATION of exact status | `NotAuthorized` |
| 429 | rate limit (gateway) | `RateLimited` |
| 5xx | `{"error":{"code":"SYSTEM_ERROR",...}}` | `Unavailable` |
| timeout / network | — | `Timeout` / `Unavailable` |

No rate-limit headers were observed on successful responses.

**Fallback behaviour.** Recognition failure is reported honestly ("The voice service is not available
right now. Nothing has been sent. You can type your request instead."). A transcript is never invented,
and the conversation keeps its state. If the Twi reply audio cannot be played, the phone says in English
that the Twi voice is unavailable and reads the English version. It never reads Twi text with an English
voice, and it never stays silent about a financial result.

## Live verification log (2026-09-22)

1. `GET /asr/v3/languages`, `GET /tts/v2/languages`, `GET /v2/languages` → 200.
2. `POST /v2/translate` `{"in":"You want to send 50 Ghana cedis to Ama. Say yes to continue or no to stop.","lang":"eng-twi"}`
   → `"Wopɛ sɛ wode Ghana cedis 50 kɔma Ama. Ka sɛ yiw toa so anaasɛ dabi."`. Note the lost "to stop":
   this is why financial prompts use curated templates, not runtime translation.
3. `POST /tts/v2/synthesize` Twi "Fa Ghana sidi aduonum kɔma Ama" → 157 KB WAV.
4. That audio → `POST /asr/v3/transcribe?language=twi` → `"Fahana si de aduonum ma Ama."`.
   Currency words are garbled; the number word (`aduonum` = 50) and the name survive. The intent parser
   is designed around exactly this (see `IntentParser`, and the unit test using this exact string).

## Implementation map

| Concern | Code |
|---|---|
| Ports (provider-independent) | `Application/Speech/SpeechAbstractions.cs`: `ISpeechRecognitionProvider`, `ITranslationProvider`, `ITextToSpeechProvider` |
| Khaya HTTP client | `Infrastructure/Speech/Khaya/KhayaClient.cs` |
| Adapters | `Infrastructure/Speech/Khaya/KhayaProviders.cs` |
| Server vs phone voice, caching | `Application/Speech/SpeechService.cs`, `Infrastructure/Speech/SpeechSupport.cs` (`FileSpeechClipStore`) |
| Twi replies | `Application/Language/ResponseCatalog.cs`, `TwiWords.cs` |
| Twi understanding | `Application/Language/IntentParser.cs`, `NumberWords.cs`, `Lexicon.cs` |
| Tests (Khaya mocked) | `tests/.../Providers/KhayaClientTests.cs` |

**Prepare the demo voice**: Settings → Presenter tools → "Prepare Twi voice for the demo"
(`POST /api/v1/demo/prewarm-speech`). This synthesizes about 9 scripted replies once (≤ 9 Khaya calls);
after that, the scripted Twi demo is served entirely from the cache.

## Twi review checklist (REQUIRED before judging)

The Twi templates, number words, command words and month names were drafted for this prototype by a
non-native speaker and cross-checked against Khaya's own output where possible. **A native Twi speaker must
review them before the demo**:

- [ ] `ResponseCatalog.cs`: every `["tw"]` template, especially `CONFIRM_SEND`, `AUTH_REQUIRED`, `SUCCESS`, `FAILED`, `UNKNOWN`
- [ ] `TwiWords.cs`: numerals (aduonum = 50, ɔha = 100, apem = 1000, "ne" joining), digit words, months
- [ ] `Lexicon.cs`: yes/no/cancel/repeat/help words (aane, daabi, gyae, san ka, boa me)
- [ ] `NumberWords.cs`: recogniser spelling variants, to be added as they are observed
- [ ] `mobile/src/lib/strings.ts`: `tw` UI labels
- [ ] After edits: delete `.cache/tts` and run "Prepare Twi voice for the demo" again
