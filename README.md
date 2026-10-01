# swiftbets-payments

Deposits and withdrawals for SwiftBets: a provider port, a simulated provider, a Paystack test-mode adapter for deposits,
signed webhooks, a sweep that finishes payments a webhook never finished, and a daily provider-against-ledger
reconciliation that Steward watches.

## How money moves

- **Deposit:** `POST /me/deposits` → the customer pays at the provider's checkout → the provider's webhook → the wallet
  credits the deposit under the key `deposit_<id>` → the deposit is marked succeeded and `DepositSucceededV1` is published.
  - Marking is conditional on the deposit still being open, and the wallet key is fixed. So a webhook delivered twice,
    late or out of order credits once.
  - A deposit the wallet refuses (a deposit limit or restriction) fails and is refunded at the provider.
- **Withdrawal:** `POST /me/withdrawals` → identity must be verified (KYC status from compliance's compacted snapshot)
  → the wallet holds the amount → amounts above the approval threshold wait for an operator → the provider pays out →
  the hold is paid out to the funding account, or returned on failure or rejection.
  - The status changes before the wallet is told, and `HoldSettled` records that it was told. A crash in between is
    finished by the sweep.
- **Sweep** (every minute): asks the provider about open deposits and submitted withdrawals, resumes steps a crash
  interrupted, and fails deposits abandoned at checkout after 24 hours.
- **Reconciliation** (daily, after 02:00 SA time, for yesterday): the provider's settled movements against our credited
  deposits and paid withdrawals.
  - A reference only one side lists for the day is looked up on the other side, so a payment that settled either side
    of midnight is not a drift.
  - Drifts are stored and `PaymentDriftDetectedV1` is published; Steward opens an incident.

## Providers

| Name | Deposits | Payouts | Webhook signature |
|---|---|---|---|
| `simulator` | yes | yes | `X-Simulator-Signature: t=<unix>,v1=<hmac-sha256 of "t.body">`, refused outside 5 minutes |
| `paystack` (test mode) | yes | no | `x-paystack-signature`: HMAC-SHA512 of the body with the secret key |

The simulator (`SwiftBets.Payments.Simulator`) is its own host. It has fault switches (`PUT /__faults`):

- `duplicateWebhooks`, `reverseOrder`, `failTransfers`, `holdWebhooks`

`POST /__settlements` injects a settled movement the platform never saw, which gives the reconciliation something to find.

CI never calls Paystack: its adapter is tested against recorded responses.

## Endpoints

| Route | Who |
|---|---|
| `POST /me/deposits`, `GET /me/deposits/{id}`, `POST /me/withdrawals`, `GET /me/payments` | signed-in customer |
| `POST /webhooks/{provider}` | anonymous; the signature is the authentication |
| `GET /admin/payments/withdrawals?status=awaitingApproval`, `GET /admin/payments/reconciliation/{provider}/latest` | `payments.read` |
| `POST /admin/payments/withdrawals/{id}/approve|reject`, `POST /admin/payments/reconciliation/{provider}/run?day=` | `payments.approve` |

## Configuration

| Key | Meaning |
|---|---|
| `ConnectionStrings:SbPayments` | SQL Server database |
| `Wallet:GrpcAddress`, `ServiceIdentity:*` | the wallet's gRPC API, with client-credentials tokens from identity |
| `Payments:DepositProvider`, `Payments:WithdrawalProvider` | `simulator` (default) or `paystack` for deposits |
| `Payments:ReturnUrl` | where checkout sends the customer back (`?deposit=<id>` is appended) |
| `Payments:Simulator:BaseUrl`, `:ApiKey`, `:WebhookSecrets:0..n` | the simulator; several secrets while rotating |
| `Payments:Paystack:SecretKey` | test-mode secret key; Paystack is registered only when it is set |

Amount bounds and the approval threshold per currency are in `PaymentRules`:

- **ZAR:** deposits R10–R50 000; withdrawals R50–R100 000; approval above R5 000.
- **USD:** deposits $1–$3 000; withdrawals $5–$5 000; approval above $300.

## Build and test

```sh
dotnet build
dotnet test   # SQL Server tests run in Testcontainers
```

Migrations are numbered with rollbacks: `0001` schema, `0002` deposits, withdrawals and webhook events, `0003` reconciliation.
