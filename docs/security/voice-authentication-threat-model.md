# Voice and authentication: threat model

## Decision

**In Kasa Sika, voice is an interface, never a credential.** What you say can start and shape a request.
It can never prove who you are and it can never approve money movement.

Approval needs a separate, transaction-bound step-up on the device:

1. **Kasa Sika passcode**, typed on an accessible keypad (6 digits, not the MoMo PIN), or
2. **Device biometrics** (fingerprint/face) releasing an enrolled device key that signs a challenge
   naming the exact amount, currency and recipient.

A third method, `DemoSimulated` (one tap), exists for presentations only. It is off by default, and the
API refuses to enable it unless payments are mock or sandbox.

## Why not a spoken secret?

The brief allows a spoken secret if it is clearly labelled a prototype. We decided against it:

| Problem | Why it matters here |
|---|---|
| Overheard | The users this is for often do their banking with others nearby. Saying a secret aloud hands it to everyone in the room, which is the very dependence Kasa Sika exists to remove. |
| Sent to the speech provider | Recognising a spoken secret means sending it to the ASR service (Khaya). The brief forbids exposing authentication secrets to speech providers. |
| Recorded | Anything spoken can be recorded and replayed. |
| Low entropy | Short phrases are guessable, and recognition errors force weak matching. |

## Why not voice biometrics (speaker verification) in this prototype?

| Attack | Speaker verification without liveness |
|---|---|
| Replay of a recording | Accepted |
| Voice cloning (a few seconds of public audio suffice) | Frequently accepted |
| Synthetic / TTS voices | Frequently accepted |
| Illness, age, noise (common for our users) | Frequently *rejected*: excludes the people we serve |

Building it without a certified liveness-detecting provider would be security theatre. The
`IStepUpAuthenticator` port lets a certified provider be added later, and it would be combined with
(not replace) a device-bound factor. No `voice_credentials` table was created: storing voiceprints
without a verifier would only add sensitive data.

## What protects against voice attacks today

| Attack | Outcome |
|---|---|
| Someone shouts "send 500 cedis to Kofi" near the phone | A draft is created and read back aloud. It needs "yes" and then the owner's fingerprint or passcode. |
| Replayed recording of the owner saying "yes" | Confirms a draft, but step-up is still required and cannot come from audio. |
| Cloned voice of the owner | Same as above. |
| Hidden or ultrasonic commands in media | Same as above; plus every draft is spoken back, so the owner hears it. |
| Voice command for a different amount after confirmation | The confirmed draft is cancelled; the new amount needs a new confirmation and step-up. The submit step re-checks the authenticated fingerprint. |

## Transaction binding

`authentication_challenges` rows bind: user · transaction · amount · currency · recipient (keyed
fingerprint) · device (if known) · expiry (5 minutes) · single use. `authentication_attempts` rows record the
same fields plus method and result for every attempt, successful or not.

The biometric signature is `HMAC-SHA256(deviceKey, "kasa-sika-v1|challengeId|nonce|transactionId|amountMinor|currency|maskedRecipient")`.

## Known prototype limits

- The device key is a shared secret (encrypted at rest on the server), and is released after the OS
  biometric prompt in app code. Production: a hardware-backed, non-exportable asymmetric key (Android
  Keystore / Secure Enclave) or a passkey, with the server holding only the public key.
- The biometric path was built against the Expo SDK 57 APIs but **has not been tested on a physical
  device**. The passcode path is tested end to end.
