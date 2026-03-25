# Change Request: Trading Account-Centric Architecture & Trader Authentication

## Summary

Restructure the engine around **TradingAccount as the central entity** (retail MT5 accounts), remove the legacy Broker entity entirely, and introduce a **trader login flow** where retailers authenticate using their MT5 account ID + broker server (with optional password).

---

## Current State

### TradingAccount Entity (`Domain/Entities/TradingAccount.cs`)
- Already has: `AccountId`, `BrokerServer`, `BrokerName`, `HashedPassword`, `Leverage`, `AccountType`, `MarginMode`, `Currency`, `Balance`, `Equity`, `MarginUsed`, `MarginAvailable`, `IsActive`, `IsPaper`, `LastSyncedAt`
- **Problem:** Several Application-layer files still reference a `BrokerId` FK that no longer exists on the entity:
  - `CreateTradingAccountCommand` has `BrokerId` property and validates it
  - `GetActiveTradingAccountQuery` filters by `x.BrokerId`
  - `TradingAccountDto` exposes `BrokerId`
  - `TradingAccountController.GetActive()` takes `brokerId` as route param

### Broker Entity (REMOVED)
- `Broker.cs` source file no longer exists in `Domain/Entities/`
- Broker-related enums still exist: `BrokerType`, `BrokerEnvironment`, `BrokerStatus`
- Stale files still reference the old Broker entity:
  - `API/Controllers/v1/BrokerController.cs` (full CRUD, references non-existent Application commands/queries)
  - `API/Controllers/v1/BrokerManagementController.cs`
  - `UnitTest/Application/Brokers/CreateBrokerCommandTest.cs`

### Authentication
- JWT-based auth via `AuthControllerBase` (shared library)
- `AuthTokenController` exists but is dev-only (generates test tokens)
- No production login endpoint exists
- No concept of "trader identity" tied to TradingAccount

### Encryption
- `BrokerKeyEncryption` (AES-256-GCM) already exists at `Application/Common/Security/BrokerKeyEncryption.cs`
- Uses `enc:` prefix, derives key from config `Encryption:Key` via SHA256
- Can be reused for password encryption

---

## Changes Required

### Phase 1: Clean Up Broker Remnants

#### 1.1 Delete stale Broker files
| Action | File |
|--------|------|
| DELETE | `API/Controllers/v1/BrokerController.cs` |
| DELETE | `API/Controllers/v1/BrokerManagementController.cs` |
| DELETE | `UnitTest/Application/Brokers/CreateBrokerCommandTest.cs` |
| DELETE | `Domain/Enums/BrokerType.cs` |
| DELETE | `Domain/Enums/BrokerEnvironment.cs` |
| DELETE | `Domain/Enums/BrokerStatus.cs` |

#### 1.2 Remove BrokerId references from TradingAccount feature
| File | Change |
|------|--------|
| `Application/TradingAccounts/Commands/CreateTradingAccount/CreateTradingAccountCommand.cs` | Remove `BrokerId` property and its validation rule. Replace with `BrokerServer`, `BrokerName`, `Password` (optional) fields. |
| `Application/TradingAccounts/Commands/CreateTradingAccount/CreateTradingAccountCommand.cs` (Handler) | Remove `BrokerId` assignment. Add `BrokerServer`, `BrokerName` mapping. Encrypt and store default password via `BrokerKeyEncryption`. |
| `Application/TradingAccounts/Queries/GetActiveTradingAccount/GetActiveTradingAccountQuery.cs` | Remove `BrokerId` filter. Change to find active account by the authenticated user's context (or remove if redundant with the new login flow). |
| `Application/TradingAccounts/Queries/DTOs/TradingAccountDto.cs` | Remove `BrokerId`. Add `BrokerServer`, `BrokerName`, `AccountType`, `Leverage`, `MarginMode`. |
| `API/Controllers/v1/TradingAccountController.cs` | Update `GetActive` endpoint — remove `brokerId` route param. |

#### 1.3 Audit other BrokerId/Broker FK references
Search the entire codebase for any remaining `BrokerId` or `Broker` navigation property references in handlers, workers, services, and configurations. Clean up or redirect to TradingAccount properties.

#### 1.4 Database migration
- Create migration to drop the `Brokers` table (if it still exists in the DB schema)
- Remove `BrokerId` column from `TradingAccounts` table (if it still exists)
- Ensure `BrokerServer` and `BrokerName` columns exist (they are already on the entity)

---

### Phase 2: TradingAccount Password Management

#### 2.1 Password field on TradingAccount
The entity already has `HashedPassword`. Rename to `EncryptedPassword` for clarity (this is AES-encrypted, not hashed — it must be decryptable).

| File | Change |
|------|--------|
| `Domain/Entities/TradingAccount.cs` | Rename `HashedPassword` -> `EncryptedPassword`. |
| `Infrastructure/Persistence/Configurations/TradingAccountConfiguration.cs` | Update column mapping if needed. |
| Migration | Rename column `HashedPassword` -> `EncryptedPassword`. |

#### 2.2 Default password generation
- When a TradingAccount is created without a password, generate a default password (e.g., a random 16-char alphanumeric string)
- Encrypt it using `BrokerKeyEncryption.Encrypt()` before storing
- The encryption key comes from config `Encryption:Key`

#### 2.3 Change password command
Create: `Application/TradingAccounts/Commands/ChangePassword/`

```
ChangePasswordCommand
  - TradingAccountId: long
  - CurrentPassword: string (optional — admin override)
  - NewPassword: string

ChangePasswordCommandHandler
  - Decrypt stored password, verify CurrentPassword matches (if supplied)
  - Encrypt NewPassword via BrokerKeyEncryption and save

ChangePasswordCommandValidator
  - NewPassword: NotEmpty, MinLength(8), MaxLength(128)
```

#### 2.4 Controller endpoint
| Controller | Endpoint | Method |
|-----------|----------|--------|
| `TradingAccountController` | `PUT {id}/password` | Change account password |

---

### Phase 3: Trader Login & Authentication

#### 3.1 Two login contexts

The engine serves two client types with different password requirements:

| Client | Endpoint | Password Required | Reason |
|--------|----------|-------------------|--------|
| **MT5 EA** (platform login) | `POST /auth/login` | No | EA authenticates via AccountId + BrokerServer only. The MT5 platform itself handles broker authentication. |
| **Web Dashboard** (customer portal) | `POST /auth/login` | Yes | Traders logging into the web dashboard must supply their password for security. |

Both use the same endpoint. The handler checks: if `Password` is supplied, validate it. If omitted and the request originates from an EA context (e.g. a specific `LoginSource` field or header), allow passwordless login. If omitted from a web context, reject.

#### 3.2 Registration endpoint (with auto-login)
Create: `Application/TradingAccounts/Commands/RegisterTrader/`

This is a **separate endpoint** for trader self-registration. On success it **auto-creates the TradingAccount and returns a JWT token** so the trader is immediately logged in.

```
RegisterTraderCommand
  - AccountId: string       (MT5 account number, e.g., "12345678")
  - BrokerServer: string    (MT5 broker server, e.g., "MetaQuotes-Demo")
  - BrokerName: string      (e.g., "MetaQuotes", "ICMarkets")
  - AccountName: string?    (optional display name, defaults to "Account {AccountId}")
  - Password: string?       (optional — if omitted, a default password is generated and encrypted)
  - Currency: string        (default "USD")
  - AccountType: string     (e.g., "demo", "real")

RegisterTraderCommandValidator
  - AccountId: NotEmpty, MaxLength(100)
  - BrokerServer: NotEmpty, MaxLength(200)
  - BrokerName: NotEmpty, MaxLength(100)
  - Password: MinLength(8), MaxLength(128) when supplied
  - Currency: MaxLength(3)
  - Unique constraint: AccountId + BrokerServer combination must not already exist

RegisterTraderCommandHandler
  1. Check if AccountId + BrokerServer already exists -> return error "-11" "Account already registered"
  2. Create TradingAccount entity:
     - Set AccountId, BrokerServer, BrokerName, AccountName, Currency, AccountType
     - If Password supplied: encrypt via FieldEncryption and store in EncryptedPassword
     - If Password omitted: generate random 16-char password, encrypt, store
     - IsActive = true (first account is auto-activated)
  3. Save to DB
  4. Generate JWT token with trading account claims
  5. Return token + account summary (auto-login)
  NOTE: The default/supplied password is NEVER returned in the response.
        Traders must set their password via PUT /trading-account/{id}/password
        before they can login on the web dashboard.
```

**Response:**
```json
{
  "data": {
    "token": "eyJ...",
    "expiresAt": "2026-03-24T00:00:00Z",
    "tokenType": "Bearer",
    "account": {
      "id": 1,
      "accountId": "12345678",
      "accountName": "Account 12345678",
      "brokerServer": "MetaQuotes-Demo",
      "brokerName": "MetaQuotes",
      "accountType": "demo",
      "currency": "USD"
    }
  },
  "status": true,
  "message": "Successful",
  "code": "00"
}
```

#### 3.3 Login endpoint
Create: `Application/TradingAccounts/Commands/LoginTradingAccount/`

```
LoginTradingAccountCommand
  - AccountId: string       (MT5 account number)
  - BrokerServer: string    (MT5 broker server)
  - Password: string?       (required for web dashboard, optional for EA/platform login)
  - LoginSource: string     (e.g., "ea" or "web" — determines password requirement)

LoginTradingAccountCommandHandler
  1. Look up TradingAccount by AccountId + BrokerServer (unique combination)
  2. If not found, return error "-14"
  3. If account IsActive == false, return error "-11" "Account is deactivated"
  4. If LoginSource == "web":
     - Password MUST be supplied, reject if missing
     - Decrypt stored EncryptedPassword, compare with supplied Password
     - If mismatch, return error "-11" with "Invalid credentials"
  5. If LoginSource == "ea":
     - Password is not required (MT5 platform handles broker auth)
     - If Password IS supplied, still validate it (defence in depth)
  6. On success:
     - Generate JWT token (one token per account, never multi-account)
     - Return token + expiry + account summary
```

**Response:** Same structure as registration response above.

#### 3.4 Controller
Create: `API/Controllers/v1/TradingAccountAuthController.cs`

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `POST /api/v1/lascodia-trading-engine/auth/register` | RegisterTraderCommand | `[AllowAnonymous]` | Trader self-registration + auto-login |
| `POST /api/v1/lascodia-trading-engine/auth/login` | LoginTradingAccountCommand | `[AllowAnonymous]` | Trader / EA login |
| `POST /api/v1/lascodia-trading-engine/auth/refresh` | (future) | `[AllowAnonymous]` | Token refresh |

#### 3.5 JWT claims update
Update token generation to include trading account context:

| Claim | Source |
|-------|--------|
| `tradingAccountId` | TradingAccount.Id (DB PK) |
| `accountId` | TradingAccount.AccountId (MT5 account number) |
| `brokerServer` | TradingAccount.BrokerServer |
| `accountName` | TradingAccount.AccountName |

#### 3.6 One token per account
Each JWT token is scoped to a single TradingAccount. There is no multi-account token. If a trader has multiple MT5 accounts, they must login separately for each and receive separate tokens.

#### 3.7 EA authentication remains separate
The EA continues to use its existing auth flow (registers via `POST /ea/register` with JWT). The new login endpoint serves as how the EA **obtains** its JWT — by calling `POST /auth/login` with `LoginSource: "ea"` and no password. The EA registration endpoint itself remains unchanged.

#### 3.8 CurrentUserService extension
Extend `ICurrentUserService` (or create `ITradingAccountContext`) to extract `TradingAccountId` from the JWT claims so that handlers can scope data to the authenticated account.

> **Note:** This may require changes to the shared library. If so, the changes should be made in the shared library source repo and the submodule bumped — never edited directly.

---

### Phase 4: Rename BrokerKeyEncryption

| File | Change |
|------|--------|
| `Application/Common/Security/BrokerKeyEncryption.cs` | Rename class to `FieldEncryption` (it's now general-purpose, used for passwords too). Rename file to match. |
| All references | Update usages across the codebase. |

---

### Phase 5: Update CLAUDE.md

Update the architecture documentation to reflect:
- Broker entity removal
- TradingAccount as the central entity
- New login flow
- Updated entity list, enum list, feature list, controller list
- Remove Broker-related entries from all tables

---

## Dependency Order

```
Phase 1 (Broker cleanup)
  -> Phase 2 (Password management)
    -> Phase 3 (Login & auth)
      -> Phase 4 (Rename encryption utility)
        -> Phase 5 (Documentation update)
```

Phases 1 and 2 can partially overlap. Phase 4 is cosmetic and can be done at any time after Phase 2.

---

## Files Affected (Summary)

### Delete
- `API/Controllers/v1/BrokerController.cs`
- `API/Controllers/v1/BrokerManagementController.cs`
- `UnitTest/Application/Brokers/CreateBrokerCommandTest.cs`
- `Domain/Enums/BrokerType.cs`
- `Domain/Enums/BrokerEnvironment.cs`
- `Domain/Enums/BrokerStatus.cs`

### Modify
- `Domain/Entities/TradingAccount.cs` — rename `HashedPassword` -> `EncryptedPassword`
- `Application/TradingAccounts/Commands/CreateTradingAccount/CreateTradingAccountCommand.cs` — remove BrokerId, add broker fields + default password
- `Application/TradingAccounts/Queries/GetActiveTradingAccount/GetActiveTradingAccountQuery.cs` — remove BrokerId filter
- `Application/TradingAccounts/Queries/DTOs/TradingAccountDto.cs` — remove BrokerId, add broker fields
- `API/Controllers/v1/TradingAccountController.cs` — update GetActive endpoint, add password endpoint
- `Application/Common/Security/BrokerKeyEncryption.cs` — rename to `FieldEncryption`
- `Infrastructure/Persistence/Configurations/TradingAccountConfiguration.cs` — column rename
- `CLAUDE.md` — full documentation update

### Create
- `Application/TradingAccounts/Commands/RegisterTrader/RegisterTraderCommand.cs`
- `Application/TradingAccounts/Commands/LoginTradingAccount/LoginTradingAccountCommand.cs`
- `Application/TradingAccounts/Commands/ChangePassword/ChangePasswordCommand.cs`
- `API/Controllers/v1/TradingAccountAuthController.cs`
- New EF migration for Broker table drop + column renames

### Audit (check for remaining Broker references)
- All workers, services, and handlers that may reference `BrokerId` or Broker navigation properties
- `Infrastructure/HealthChecks/TradingEngineHealthChecks.cs` (`BrokerHealthCheck` class)
- `Application/Services/BrokerAdapters/*` — review if these need Broker entity or just TradingAccount
- `Application/ExpertAdvisor/Commands/ProcessReconciliation/ProcessReconciliationCommand.cs` — has `BrokerPositionItem`/`BrokerOrderItem` (these are DTOs for MT5 data, likely fine to keep the naming)

---

## Decisions (Resolved)

1. **Password requirement depends on context:** MT5/EA login is passwordless (the platform handles broker auth). Web dashboard login requires the password. Controlled via `LoginSource` field on the login command.
2. **One token per account:** JWT tokens are scoped to a single TradingAccount. No multi-account tokens.
3. **EA gets its JWT via the login endpoint:** EA calls `POST /auth/login` with `LoginSource: "ea"` (no password). The existing EA registration flow (`POST /ea/register`) remains unchanged — it just requires a valid JWT.
4. **Separate registration endpoint with auto-login:** `POST /auth/register` creates the TradingAccount and returns a JWT in one step. Traders do not need to register then login separately.

5. **Rate limiting:** Auth endpoints use the same `"auth"` rate limit policy (10 req/min). No stricter limits needed.
6. **Deactivated accounts cannot login:** If `IsActive = false`, the login handler rejects with error "-11" "Account is deactivated". This applies to both EA and web logins.
7. **Default password not returned:** When a default password is auto-generated during registration, it is NOT included in the response. The trader must set their own password via `PUT /trading-account/{id}/password` before they can login on the web dashboard. EA login remains passwordless regardless.
