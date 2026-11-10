# MTN MoMo integration

**Kasa Sika authenticates the person. MTN authorizes the money.**

MTN is the financial system of record: wallets, money, authorization, provider-side validation, execution
and the authoritative result. Kasa Sika is an accessibility and authentication layer in front of it, and
never stores or mirrors anyone's balance.

Contracts below were verified on **2026-09-22** from the MTN MoMo developer portal
(https://momodeveloper.mtn.com, an Azure API Management portal) using its public API definitions.
No call has been made to MTN with the project's credentials yet (the keys were not provided to the
tooling). Statements about production behaviour are marked **REQUIRES_MTN_CONFIRMATION**.

## Subscription

| Item | Value |
|---|---|
| Product | Disbursements |
| Subscription | `kasa_sika` |
| State | Active (started 2026-09-14) |
| Keys | Primary / secondary subscription keys: to be stored **only** as `Mtn:SubscriptionKey` in `dotnet user-secrets` (or env `Mtn__SubscriptionKey`) |
| API user / key | Sandbox: created with the provisioning command below. Production: issued by MTN. |

## Operations (Disbursements, as listed by the portal)

`bc-authorize`, `CreateAccessToken`, `CreateOauth2Token`, `Deposit-V1`, `Deposit-V2`, `GetAccountBalance`,
`GetAccountBalanceInSpecificCurrency`, `GetBasicUserinfo`, `GetDepositStatus`, `GetRefundStatus`,
`GetTransferStatus`, `GetUserInfoWithConsent`, `Refund-V1`, `Refund-V2`, `Transfer`,
`ValidateAccountHolderStatus`. Also Sandbox User Provisioning: `POST /v1_0/apiuser`,
`GET /v1_0/apiuser/{id}`, `POST /v1_0/apiuser/{id}/apikey`.

Host: `https://sandbox.momodeveloper.mtn.com`. Every call carries `Ocp-Apim-Subscription-Key`.

## What `MtnMomoProvider` implements

| Step | MTN operation | Verified contract | Kasa Sika behaviour |
|---|---|---|---|
| Access token | `POST /disbursement/token/` | `Authorization: Basic base64(apiUser:apiKey)` → `{access_token, token_type, expires_in}` | Cached until 60 s before expiry; refreshed once on a 401 |
| Validate recipient | `GET /disbursement/v1_0/accountholder/{type}/{id}/active` | Bearer + `X-Target-Environment`; 200 "true if registered and active" | `msisdn/233XXXXXXXXX`; accepts `{"result":bool}` or a bare bool; 400/404 = not active; 5xx/timeout = provider unavailable (the transfer goes back to PARSED; nothing is sent) |
| Consent | — | Transfer has no consumer consent | Recorded as `NOT_REQUIRED`, with the reason on the timeline |
| Transfer | `POST /disbursement/v1_0/transfer` | Headers `X-Reference-Id` (UUID), `X-Target-Environment`, optional `X-Callback-Url`; body `{amount, currency, externalId, payee{partyIdType:"MSISDN", partyId}, payerMessage, payeeNote}` → **202 Accepted** | Reference = transaction id (MTN's idempotency key); externalId = same. 202 → `SUBMITTED` (never "success"). 409 `RESOURCE_ALREADY_EXIST` → MTN already has it (accepted, no duplicate). 4xx → `FAILED` (rejected). 500 with a documented definitive code (`INVALID_CURRENCY`, `PAYEE_NOT_FOUND`, ...) → `FAILED`. Other 5xx, timeout or connection loss → `UNKNOWN` (reconciled, never resubmitted). |
| Status | `GET /disbursement/v1_0/transfer/{referenceId}` | 200 `{status: SUCCESSFUL\|FAILED\|PENDING, reason{code}, financialTransactionId}`; 404 `RESOURCE_NOT_FOUND` | Mapped to SUCCESS / FAILED / PENDING; unrecognised → UNKNOWN; 404 after the grace period for an uncertain submission → FAILED ("provider has no record"); a failed status *check* changes nothing |
| Balance | `GET /disbursement/v1_0/account/balance` | `{availableBalance, currency}` | Available to operators only. It is **Kasa Sika's own disbursement account**, not the user's wallet, so it is never presented as "your balance". |
| Callback | `POST/PUT {X-Callback-Url}` | MTN posts the transfer result, which includes our `externalId` | `/api/v1/callbacks/mtn`. Treated as an untrusted nudge: only the reference is read, and the status is re-fetched from MTN. Fingerprinted, so a duplicate is recorded once and changes nothing. |

## The honest limitation: whose money moves?

The portal documents `Transfer` as moving money **"from the own account to a payee account"**: from the
API user's account, which is Kasa Sika's disbursement account. In MTN sandbox mode, "Send 50 cedis to Ama"
therefore exercises the real integration (token, validation, transfer, status, callbacks), but the funds
come from Kasa Sika's sandbox account, not from the user's wallet.

The production path for a user sending **their own** money is one of the following, all
**REQUIRES_MTN_CONFIRMATION**:

1. **Consent-based transfer on the user's behalf** (`bc-authorize` → `CreateOauth2Token` with the CIBA
   grant `urn:openid:params:grant-type:ciba`). The documented contract: `bc-authorize` takes
   `login_hint=ID:{msisdn}/MSISDN&scope={scope}&access_type={online|offline}` and returns
   `{auth_req_id, interval, expires_in}`; the OAuth2 token endpoint exchanges `auth_req_id` for a
   user-scoped token. MTN also documents consumer-consent products (TransferWithConsent,
   PaymentWithConsent, ...). Which of these is available to Kasa Sika in Ghana, with which scopes, is
   REQUIRES_MTN_CONFIRMATION.
2. **Collections `RequestToPay`** (debit the user, who approves with MTN's own PIN prompt), followed by a
   disbursement. Needs the Collections product.

In both, **MTN's own authorization step is where the MoMo PIN is used**. Kasa Sika never sees it. That is
exactly the "Kasa Sika authenticates the person, MTN authorizes the money" split, and the state machine
already has `AUTHORIZATION_PENDING`/`AUTHORIZED` and a `consents` table for it.

## Sandbox specifics

- `X-Target-Environment: sandbox`.
- Currency: the sandbox rejects local currencies (`INVALID_CURRENCY`) and is widely documented as
  EUR-only. Kasa Sika sends the configured `Mtn:Currency` (default `EUR`) and says so in the UI
  ("paid … in EUR"). Ghana production currency GHS is REQUIRES_MTN_CONFIRMATION.
- Startup refuses anything but `sandbox` as the target environment, and refuses a non-sandbox base URL.

## Running against the sandbox

```bash
cd backend
dotnet user-secrets set "Mtn:SubscriptionKey" "<primary key from the MTN portal>" --project src/KasaSika.Api
dotnet user-secrets set "Payments:Provider" "MtnSandbox" --project src/KasaSika.Api
dotnet run --project src/KasaSika.Api -- provision-mtn-sandbox <your-public-callback-host>
# prints two `dotnet user-secrets set` commands for Mtn:ApiUser and Mtn:ApiKey - run them
dotnet run --project src/KasaSika.Api
```

For callbacks, the API must be reachable from the internet (for example a tunnel), and
`Payments:CallbackBaseUrl` set to that public URL. Without callbacks, status polling and the reconciler
still resolve every transfer.

## Code map

| Concern | Code |
|---|---|
| Port | `Application/Abstractions/IPaymentProvider.cs` |
| MTN adapter, token cache | `Infrastructure/Payments/Mtn/MtnMomoProvider.cs` |
| Sandbox provisioning | `Infrastructure/Payments/Mtn/MtnSandboxProvisioner.cs` |
| Mock (same pipeline) | `Infrastructure/Payments/MockPaymentProvider.cs` |
| Tests | `tests/.../Providers/MtnMomoProviderTests.cs` |

See also the [capability matrix](mtn-capability-matrix.md).
