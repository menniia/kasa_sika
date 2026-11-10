# Demo script (about 4 minutes)

## Before you go on stage (10 minutes before)

1. `scripts/dev-postgres.ps1` (or `docker compose up -d`), then start the API: `dotnet run --project backend/src/KasaSika.Api`.
2. Start the app: `cd mobile && npx expo start`. Open it on the demo phone (same Wi-Fi) or in Chrome.
3. Landing → choose **Twi** → **Continue with demo account**.
4. Settings → Presenter tools:
   - **Reset demo** (clears history, conversations and lockouts).
   - **Prepare Twi voice for the demo** (once per Twi template change; plays instantly afterwards).
   - Scenario: **Per recipient (default)**.
   - Hands-free: on. Speaking speed: 0.9.
5. Phone volume up. Test one English request. **Reset demo** again.
6. Know the passcode: **246810** (demo account only).

Recipients (mock): **Ama Owusu** → success · **Kwame** → stays processing · **Kofi Mensah / Kofi Boateng** → ambiguity ·
**Kojo** → timeout then resolved · **Esi** → unknown · **Abena Mensah** → MTN failure · **Yaw** → not a MoMo account.

## The script

**1. Problem (30 s)**
> "For many people, using mobile money still means seeing the screen, reading text, navigating menus,
> and entering credentials. When the interface becomes inaccessible, people hand their phone, and often
> their PIN, to someone else. That is an accessibility problem, and it is a financial-security problem."

**2. What Kasa Sika is (20 s)**
> "Kasa Sika — *kasa* means speak, *sika* means money — lets you use MTN Mobile Money by voice, in your
> language, without handing your phone to anyone. Kasa Sika authenticates the person. MTN authorises
> the money."

Point at the banner: "Everything you see today runs for real, on mock payments. No real money moves."

**3. English, eyes on screen (40 s)** — language EN. Tap **Start**, then say:
> "Send 50 cedis to Ama."

Kasa Sika: "You are about to send 50 Ghana cedis to Ama Owusu, number ending 0 1 1 1. Say yes to continue…"
Say **"Yes."** → the double beep and the verification sheet: type **246810**.
Kasa Sika: "…Your transaction is being processed…" then "Your 50 Ghana cedis transfer to Ama Owusu was successful."
> "Notice it read back the last four digits, it never guessed, and 'accepted by MTN' was never called 'success'."

**4. The moment: phone turned away, in Twi (70 s)**
Switch the header to **TWI**. Settings → **Screen curtain** on (the screen goes black). **Turn the phone
away from the judges.**
> "Now imagine you cannot see the screen at all."

Tap anywhere on the black screen. Kasa Sika greets in Twi. Say:
> "Fa sidi aduonum kɔma Ama."

Kasa Sika (Twi): "Worebɛfa Ghana sidi aduonum akɔma Ama Owusu, ne nɔma a ɛwie…"
Say **"Aane."** → double beep → type **246810** on the keypad by position (the way blind users type PINs today).
Kasa Sika (Twi): "…Wo sika soma no rekɔ so…" then "Wo Ghana sidi aduonum a wode kɔmaa Ama Owusu no akɔ yie."

Triple-tap the top-left corner to lift the curtain. **Turn the phone around** → the same transfer, with
the trust card: what you asked, what you authorised, what the provider reports.
> "The Twi was recognised and spoken by Khaya AI. The money logic never depended on the language model."

**5. It does not guess (30 s)** — EN, say:
> "Send 20 cedis to Kofi."

"I found 2 people called Kofi… Which one?" → "Two." → read-back → say **"Send 30 cedis instead"** → "Okay, I have changed it…" → **"Cancel."**
> "Two people with the same name: it asks. You change your mind: it re-confirms. You cancel: nothing is sent."

**6. Honesty under failure (20 s)** — say "Send 25 cedis to Kojo", yes, passcode.
"I cannot confirm the result of this transaction yet. I have not marked it as successful or failed…"
A few seconds later the reconciler resolves it.
> "A timeout is where a careless system sends money twice. We ask MTN about the same reference, and never resend."

**7. Architecture (30 s)** — show `docs/architecture/system-architecture.md` or say:
> "Expo app, .NET API, PostgreSQL for application state (no balances, ever), Khaya for Twi, and an MTN
> Disbursements adapter built on the verified contract, with a mock that runs the same pipeline.
> Every step is a state in PostgreSQL, and every attempt is audited."

**8. Adoption & scale (20 s)**
> "This is for everyday use: send, check what you sent, hear results later, in your language. Ewe, Ga
> and Dagbani are configuration away. And the same layer can sit in front of any bank or wallet."

Close: **"Your money. Your voice. Your control."**

## If something goes wrong

- Khaya slow or down: Kasa Sika says so and offers typing. Switch to English (the phone's own recogniser)
  or use "Type instead". Nothing is lost.
- Wi-Fi drops: "You are offline. I have not sent the transaction." Show that as a feature.
- Lockout after wrong passcodes: Presenter tools → Reset demo.
