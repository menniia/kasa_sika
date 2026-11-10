# Q&A preparation

Short answers first; details after the dash.

**What problem are you solving?**
People who cannot reliably use mobile-money screens (blind, low-vision, elderly, low-literacy, motor
impairments) end up handing their phone and PIN to someone else. Kasa Sika lets them do it themselves, by voice.

**Why is accessibility a security problem?**
Every time someone else operates your phone, they learn your PIN and see your money. Inaccessible
interfaces force that. Removing the need for a helper removes the biggest attack.

**Why not just use MTN's existing app?**
It assumes you can see and read a screen. USSD menus assume you can read and remember deep menus
quickly. Kasa Sika is a voice-first accessibility and authentication layer *in front of* MTN, not a
replacement.

**Is Kasa Sika another mobile-money provider?**
No. No wallet, no ledger, no balances. MTN holds and moves the money and gives the authoritative result.

**Who owns the money? Where is it stored?**
The user, at MTN. Kasa Sika's database stores application state (requests, confirmations, audit), never balances.

**How does authentication work?**
After you confirm by voice, you must pass a step-up check on the phone: your fingerprint (which unlocks a
device key) or a 6-digit Kasa Sika passcode on a keypad. It is not your MoMo PIN, and it is never spoken.
The check is bound to that exact amount and recipient, single-use, and expires in 5 minutes.

**How do you prevent someone replaying a voice recording?**
Voice is never a credential. A recording can at most start a request; it still has to be confirmed and
approved on the device. We deliberately did not build voice biometrics without liveness detection:
cloning makes it unsafe.

**What happens if the phone is stolen?**
The thief still needs the fingerprint or passcode for every transfer; 3 wrong tries lock it; limits cap
the damage; sessions can be revoked server-side. The MoMo PIN is never on the phone because of us.

**What happens if the network fails?**
Offline, the app says "You are offline. I have not sent the transaction." If the network drops *during*
submission, the transfer is UNKNOWN, and we ask MTN about the same reference until it is certain.

**What happens if MTN returns 202?**
202 means "received", so we record SUBMITTED and say "being processed". Only MTN's SUCCESSFUL becomes success.

**What if the callback never arrives?**
Callbacks are only a hint. The app polls, and a background reconciler keeps asking MTN until it has an answer.

**What if a transaction is duplicated?**
It cannot be sent twice: an idempotency key per request, the MTN reference fixed before the call, and a
database gate where only one of several simultaneous submissions proceeds. Duplicate callbacks are
recognised and ignored. There is a test for each.

**Why Twi? Why Khaya AI? Why not a generic global speech model?**
Twi is the most widely spoken Ghanaian language. Global models do not recognise Twi, and phone voices
cannot speak it. Khaya is built for Ghanaian languages (ASR, TTS and translation for Twi and 30+ others),
and it recognised our real Twi request. We also designed the parser for real recogniser output, not for
perfect sentences.

**How will you support more Ghanaian languages?**
Configuration plus a reviewed reply catalogue per language. Ewe, Ga and Dagbani are already configured
with their Khaya codes, disabled until the replies are reviewed by native speakers.

**How does this scale?**
A stateless API with PostgreSQL, a modular monolith with clear seams, idempotent everything, and
content-addressed caching of speech. Horizontal scaling needs no redesign.

**How will you make money?**
A per-transaction or per-active-user fee paid by the provider or bank (accessibility compliance, reach to
underserved customers), and institutional licensing: the layer works in front of any wallet or bank.

**How do you gain user trust?**
Read-back of amount, name and last four digits; nothing moves without "yes" plus your own verification;
honest uncertainty ("I cannot confirm yet"); a trust card after every transfer; history you can ask about by voice.

**How do you verify the recipient?**
Only from your own contacts or a spoken number, always read back with the last four digits, and
checked with MTN (account holder active) before you are asked to confirm.

**Can this work with other banks or mobile-money providers?**
Yes. Providers plug in behind one interface, with capability flags, so we never claim a feature a provider lacks.

**What is real today?**
The whole software stack, live: the app, API, PostgreSQL state machine, Khaya Twi recognition and speech,
step-up authentication, idempotency and reconciliation. The MTN Disbursements adapter is built on the
verified contract.

**What is mock or simulated?**
The payment provider in this demo (no money exists). Seven scenarios run through the same pipeline MTN uses.

**What still requires MTN approval?**
Debiting the user's own wallet via consent (bc-authorize/CIBA or consent products), the user's balance,
production credentials and GHS in production. See `docs/integrations/mtn-capability-matrix.md`.
Honest limitation: Disbursements "Transfer" pays from Kasa Sika's own account.

**Is the Twi correct?**
The recognition and the voice are Khaya's. Our reply wording was drafted for the prototype and is being
reviewed by native speakers (checklist in `docs/integrations/khaya.md`).
