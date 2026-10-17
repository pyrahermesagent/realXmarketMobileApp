# Backend-driven offers namespace creation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the signed `createOffersNamespace` GraphQL call from the mobile app and instead have the Indexer + XcavateProfileApi create the property offers namespace automatically when shares become available on the secondary market (`claim_shares` or `relist_shares`).

**Architecture:** The Indexer emits a new `offers_namespace_created` webhook event for `claim_shares` and `relist_shares`. The XcavateProfileApi exposes a shared-secret webhook endpoint that receives the event, looks up the property namespace created by `init_property_asset_devnet`, and idempotently creates the `"<property> offers"` namespace. The mobile app stops calling `RealXmarketOfferClient.CreateOffersNamespaceAsync` from `PropertyDetailViewModel`.

**Tech Stack:** Rust + Carbon 0.12.0 (Indexer); .NET 10 ASP.NET Core + Hot Chocolate + EF Core (Profile API); .NET MAUI (mobile app).

**Spec:** `docs/superpowers/specs/2026-10-17-backend-offers-namespace-design.md`

## Global Constraints

- Indexer: never commit to `main`; work on `agent/<yyyymmdd>-<topic>` branch; never run `cargo fmt --all`; never hand-edit `crates/*-decoder*`; migrations are additive-only.
- Profile API: follow the existing `WebhooksController` and `NotificationWebhooksController` patterns; shared-secret auth via `INDEXER_WEBHOOK_SECRET`; idempotent handlers; 200 on redelivery/ignore, 404 when prerequisites missing, 502 on indexer resolution failure.
- Mobile app: remove only the client-side namespace creation; keep the listing refresh after transaction confirmation; the messenger fallback stays in place.

## Review Focus

1. **Profile API receives a redelivery** — must answer 200 and return the existing namespace id, not create a duplicate. (Covered by Task 4 tests.)
2. **Property namespace has not arrived yet** — the new endpoint must return 404 so the Indexer retry loop redelivers once the asset webhook lands. (Covered by Task 4 tests.)
3. **Wrong or missing `INDEXER_WEBHOOK_SECRET`** — endpoint must reject with 401/503. (Covered by Task 4 tests.)
4. **Indexer `relist_shares` fires multiple times** — event id must be transaction-scoped so legitimate re-listings are not dropped, while Profile API idempotency prevents duplicate namespaces. (Covered by Task 7 tests.)
5. **Mobile app still builds and claim flow shows no second signature** — verified by Task 11 build.

---

## Task 1: Profile API — extract offers-namespace creation helper

**Files:**
- Modify: `src/XcavateProfileApi/GraphQL/BucketMutations.cs:432-462`
- Create: `src/XcavateProfileApi/Services/Buckets/OffersNamespaceService.cs` (or add to `XcavateBuckets.Domain/Services` if it avoids an ASP.NET Core dependency)

**Interfaces:**
- Consumes: existing `NamespaceService.CreateAsync`, `BucketDbContext`, `BucketService.OffersNamespaceCategory`, `BucketMutations.OffersNamespaceName`, `BucketMutations.AssetIdPropertyKey`, `BucketMutations.ReadAssetId`.
- Produces: `Task<Namespace> FindOrCreateOffersNamespaceAsync(string caller, string propertyName, string assetId, string cluster, BucketDbContext db, NamespaceService namespaces, CancellationToken ct)` — same signature as the current private helper, but public and reusable from `WebhooksController`.

- [ ] **Step 1: Move `FindOrCreateOffersNamespaceAsync` out of `BucketMutations`**
  Extract the method (and its private dependencies `OffersNamespaceName`, `AssetIdPropertyKey`, `ReadAssetId`) into a shared location. The simplest place is a new public static class `OffersNamespaceService` in `XcavateBuckets.Domain` (the domain project already contains `NamespaceService` and `BucketService`).

- [ ] **Step 2: Update `BucketMutations.CreateOffersNamespace` and `SetupOfferBucketAsync` to call the moved helper**
  Replace the private method body with a call to `OffersNamespaceService.FindOrCreateOffersNamespaceAsync(...)`.

- [ ] **Step 3: Build the Profile API project**
  Run: `dotnet build src/XcavateProfileApi/XcavateProfileApi.csproj`
  Expected: success, no new warnings.

- [ ] **Step 4: Run existing offer-bucket tests**
  Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj --filter "FullyQualifiedName~OfferBucket"`
  Expected: existing idempotency tests still pass.

- [ ] **Step 5: Commit**
  ```bash
  git add src/XcavateProfileApi/GraphQL/BucketMutations.cs src/XcavateBuckets.Domain/Services/OffersNamespaceService.cs
  git commit -m "refactor: extract FindOrCreateOffersNamespaceAsync for reuse"
  ```

---

## Task 2: Profile API — add `OffersNamespaceCreatedPayload` model

**Files:**
- Create: `src/XcavateProfileApi/Controllers/OffersNamespaceCreatedPayload.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public sealed class OffersNamespaceCreatedPayload` with `JsonPropertyName`-pinned fields: `Event`, `ListingId`, `Listing`, `Property`, `AssetId`, `Cluster`, `TxSignature`, `Slot`, `BlockTime`.

- [ ] **Step 1: Create the payload class**
  Mirror the snake_case wire format used by `IncomeDistributedPayload` and `SharesReceivedPayload`. All fields are nullable/string/long/DateTime as appropriate.

- [ ] **Step 2: Build**
  Run: `dotnet build src/XcavateProfileApi/XcavateProfileApi.csproj`
  Expected: success.

- [ ] **Step 3: Commit**
  ```bash
  git add src/XcavateProfileApi/Controllers/OffersNamespaceCreatedPayload.cs
  git commit -m "feat: add OffersNamespaceCreatedPayload webhook model"
  ```

---

## Task 3: Profile API — extend property namespace with asset_id

**Files:**
- Modify: `src/XcavateProfileApi/Controllers/WebhooksController.cs:82-139`
- Modify: `src/XcavateProfileApi/Controllers/PropertyAssetRegisteredPayload.cs` (or inline in `WebhooksController.cs` if that is where it lives)

**Interfaces:**
- Consumes: existing `PropertyAssetRegisteredPayload` shape.
- Produces: property namespace `Properties` JSON now contains `"asset_id"` when the payload provides it.

- [ ] **Step 1: Verify `PropertyAssetRegisteredPayload` is the file/class that carries the asset registration event**
  Search `src/XcavateProfileApi/Controllers` for `PropertyAssetRegisteredPayload`. If it is only inside `WebhooksController.cs`, modify it there.

- [ ] **Step 2: Add `AssetId` property to the payload class**
  ```csharp
  [JsonPropertyName("asset_id")]
  public long? AssetId { get; set; }
  ```

- [ ] **Step 3: Store `asset_id` in the namespace properties inside `InitPropertyAssetDevnet`**
  Add `new("asset_id", payload.AssetId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)` to the `properties` list before calling `_namespaces.CreateAsync`.

- [ ] **Step 4: Build and run property-webhook tests**
  Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj --filter "FullyQualifiedName~InitPropertyAsset"`
  Expected: pass.

- [ ] **Step 5: Commit**
  ```bash
  git add src/XcavateProfileApi/Controllers/WebhooksController.cs src/XcavateProfileApi/Controllers/PropertyAssetRegisteredPayload.cs
  git commit -m "feat: store asset_id on property namespace from indexer webhook"
  ```

---

## Task 4: Profile API — implement `POST /webhooks/offers_namespace_created_devnet`

**Files:**
- Modify: `src/XcavateProfileApi/Controllers/WebhooksController.cs`
- Reuse helper from Task 1.

**Interfaces:**
- Consumes: `OffersNamespaceCreatedPayload`, `OffersNamespaceService.FindOrCreateOffersNamespaceAsync`, existing `FindNamespaceAsync`, `ProcessedWebhookEvents`, `CheckWebhookSecret` pattern from `NotificationWebhooksController`.
- Produces: `IActionResult OffersNamespaceCreatedDevnet(OffersNamespaceCreatedPayload, CancellationToken)`.

- [ ] **Step 1: Copy the shared-secret check helper into `WebhooksController`**
  Add `CheckWebhookSecret()` (same implementation as `NotificationWebhooksController`, reading `INDEXER_WEBHOOK_SECRET` from `_config`).

- [ ] **Step 2: Add the endpoint method `OffersNamespaceCreatedDevnet`**
  - Validate payload: `Event == "offers_namespace_created"`, `ListingId` or `Property` present, `Cluster` present.
  - Validate secret.
  - Build `eventKey = $"offers_namespace_created:{payload.ListingId ?? payload.Property}:{payload.TxSignature}"`.
  - Check `ProcessedWebhookEvents` for duplicate; if found return 200 `duplicate`.
  - Find property namespace via `FindNamespaceAsync(payload.ListingId, payload.Property, ct)`.
  - If not found, return 404.
  - Read `asset_id` from namespace properties (added in Task 3). If absent, fall back to `payload.AssetId`.
  - Use namespace `Name` (without the `" offers"` suffix — `OffersNamespaceService` appends it) and namespace `Creator` as caller.
  - Inside a transaction, call `OffersNamespaceService.FindOrCreateOffersNamespaceAsync(caller, propertyName, assetIdString, payload.Cluster, _db, _namespaces, ct)`.
  - Record the processed event.
  - Commit and return 200 with `namespaceId` and `status: "created"` or `status: "ignored"`.

- [ ] **Step 3: Add unit/integration tests**
  Create or extend `tests/XcavateBuckets.Tests/OffersNamespaceWebhookTests.cs` (follow `WebhooksController` test patterns if they exist; otherwise add to the existing bucket test project).
  Cover:
  - Create on first delivery.
  - `duplicate` on redelivery with same `eventKey`.
  - `ignored` when namespace already existed before the webhook.
  - 404 when property namespace is missing.
  - 401/503 on bad/missing secret.

- [ ] **Step 4: Run tests and build**
  Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj`
  Run: `dotnet build src/XcavateProfileApi/XcavateProfileApi.csproj`
  Expected: pass, no warnings.

- [ ] **Step 5: Commit**
  ```bash
  git add src/XcavateProfileApi/Controllers/WebhooksController.cs src/XcavateProfileApi/Controllers/OffersNamespaceCreatedPayload.cs tests/XcavateBuckets.Tests/OffersNamespaceWebhookTests.cs
  git commit -m "feat: add offers_namespace_created webhook endpoint"
  ```

---

## Task 5: Indexer — add `offers_namespace_created` event type

**Files:**
- Modify: `crates/indexer/src/mapping/mod.rs`
- Modify: `crates/indexer/src/config.rs`

**Interfaces:**
- Consumes: existing event-type constants.
- Produces: `pub const OFFERS_NAMESPACE_CREATED: &str = "offers_namespace_created";` and a `cluster: String` field on `Config`.

- [ ] **Step 1: Add the event type constant**
  In `crates/indexer/src/mapping/mod.rs` (or wherever `PROPERTY_CLAIM_STARTED` is declared), add:
  ```rust
  pub const OFFERS_NAMESPACE_CREATED: &str = "offers_namespace_created";
  ```

- [ ] **Step 2: Add `cluster` to `Config`**
  In `crates/indexer/src/config.rs`:
  - Add field `pub cluster: String` to `Config`.
  - Parse env var `CLUSTER` with default `"devnet"`.
  - Document in `.env.example` if one exists.

- [ ] **Step 3: Add the webhook route mapping**
  In `config.rs::webhook_routes_from_env()`, add:
  ```rust
  ("OFFERS_NAMESPACE_CREATED_WEBHOOK_URL", event_type::OFFERS_NAMESPACE_CREATED)
  ```

- [ ] **Step 4: Build**
  Run: `SQLX_OFFLINE=true cargo build --workspace --locked`
  Expected: success.

- [ ] **Step 5: Commit**
  ```bash
  git add crates/indexer/src/mapping/mod.rs crates/indexer/src/config.rs .env.example
  git commit -m "feat: add offers_namespace_created event type and cluster config"
  ```

---

## Task 6: Indexer — extend `property_asset_registered` payload with asset_id

**Files:**
- Modify: `crates/indexer/src/mapping/marketplace.rs:321-343`

**Interfaces:**
- Consumes: `MarketplaceInstruction::InitPropertyAssets(args)` and account metadata.
- Produces: payload now includes `"asset_id"`.

- [ ] **Step 1: Add `asset_id` to the payload**
  The `InitPropertyAssets` instruction args contain `listing_id`; the PropertyAsset account (index 3) stores `asset_id`. If the decoded account data is not available in `map_instruction`, use `args.listing_id` as a temporary fallback with a TODO comment, because they coincide today. Prefer sourcing `asset_id` from the PropertyAsset account state if Carbon exposes it.

- [ ] **Step 2: Add/update unit test**
  In `crates/indexer/src/mapping/sibling_tests.rs`, update the `init_property_assets` webhook assertion to expect `"asset_id"`.

- [ ] **Step 3: Run tests**
  Run: `cargo test --workspace --locked -p indexer -- mapping::sibling_tests` (or the actual test module path)
  Expected: pass.

- [ ] **Step 4: Commit**
  ```bash
  git add crates/indexer/src/mapping/marketplace.rs crates/indexer/src/mapping/sibling_tests.rs
  git commit -m "feat: include asset_id in property_asset_registered webhook payload"
  ```

---

## Task 7: Indexer — emit `offers_namespace_created` for `claim_shares` and `relist_shares`

**Files:**
- Modify: `crates/indexer/src/mapping/marketplace.rs`

**Interfaces:**
- Consumes: `Config::cluster`, `event_type::OFFERS_NAMESPACE_CREATED`, instruction metadata.
- Produces: `WebhookEvent` rows for `offers_namespace_created`.

- [ ] **Step 1: Emit for `claim_shares`**
  In the `MarketplaceInstruction::ClaimShares(args)` arm, add a second `WebhookEvent`:
  ```rust
  WebhookEvent {
      event_id: format!("offers_namespace_created:{listing_b58}"),
      event_type: event_type::OFFERS_NAMESPACE_CREATED,
      payload: serde_json::json!({
          "event": event_type::OFFERS_NAMESPACE_CREATED,
          "listing": &listing_b58,
          "property": account_at(accounts, 5, name)?,
          "listing_id": args.listing_id,
          "cluster": /* config.cluster from context, or hardcode "devnet" with TODO */,
          "slot": slot,
          "tx_signature": &tx_signature,
          "block_time": block_time.to_rfc3339(),
          "program": "marketplace",
      }),
      slot, tx_signature: tx_signature.clone(), block_time,
  }
  ```
  Note: `Config` is not directly available in `map_instruction`. Pass it as a parameter or use a thread-local/global set at startup. The cleanest short-term fix is to read a `CLUSTER` env var at module load (`std::env::var("CLUSTER").unwrap_or_else(|_| "devnet".to_string())`) and document the TODO to thread `Config` through the mapper.

- [ ] **Step 2: Emit for `relist_shares`**
  Add a new match arm for `MarketplaceInstruction::RelistShares(args)`:
  - accounts: seller 0, payer 1, config 2, seller_role 3, listing 4, holding 5, share_listing 6.
  - event_id: transaction-scoped (`offers_namespace_created:{tx_signature}:{ctx.index_str}`).
  - payload: `event`, `asset_id`: args.asset_id, `listing_id`: args.listing_id (or resolve from listing account), `share_listing`, `seller`, `amount`, `share_price`, `cluster`, `slot`, `tx_signature`, `block_time`, `program`.

- [ ] **Step 3: Add unit tests**
  In `crates/indexer/src/mapping/sibling_tests.rs`:
  - Update `claim_shares` test to assert the second `offers_namespace_created` event exists and has the expected shape.
  - Add `relist_shares` test asserting the new event.

- [ ] **Step 4: Run tests**
  Run: `cargo test --workspace --locked -p indexer -- mapping::sibling_tests`
  Expected: pass.

- [ ] **Step 5: Commit**
  ```bash
  git add crates/indexer/src/mapping/marketplace.rs crates/indexer/src/mapping/sibling_tests.rs
  git commit -m "feat: emit offers_namespace_created webhook for claim and relist"
  ```

---

## Task 8: Indexer — documentation

**Files:**
- Modify: `DECISIONS.md`
- Modify: `RUNBOOK.md`
- Modify: `.env.example` (if exists)

- [ ] **Step 1: Add ADR to `DECISIONS.md`**
  Three-paragraph format: context (mobile app second prompt), decision (backend webhook), consequences (new event type, new endpoint, mobile app simplified).

- [ ] **Step 2: Update `RUNBOOK.md` webhook setup section**
  Document `OFFERS_NAMESPACE_CREATED_WEBHOOK_URL` pointing to `https://profile-api.xcavate.io/webhooks/offers_namespace_created_devnet` with `?secret=<INDEXER_WEBHOOK_SECRET>`.

- [ ] **Step 3: Update `.env.example`**
  Add `CLUSTER=devnet` and `OFFERS_NAMESPACE_CREATED_WEBHOOK_URL=`.

- [ ] **Step 4: Commit**
  ```bash
  git add DECISIONS.md RUNBOOK.md .env.example
  git commit -m "docs: ADR and runbook for backend offers namespace creation"
  ```

---

## Task 9: Mobile app — remove client-side namespace creation

**Files:**
- Modify: `realXmarketPlutoFramework/PlutoFramework/Components/XcavateProperty/PropertyDetailViewModel.cs`

**Interfaces:**
- Consumes: existing `RefreshListingAsync`.
- Produces: `OnMarketplaceTransactionConfirmed` now only refreshes the listing; `EnsureOffersNamespaceAsync` and `offersNamespaceEnsured` removed.

- [ ] **Step 1: Remove the namespace-creation follow-up**
  Change `RefreshListingAndEnsureOffersNamespaceAsync` to:
  ```csharp
  private async Task RefreshListingAfterTransactionAsync()
  {
      await RefreshListingAsync(CancellationToken.None);
  }
  ```
  Update `OnMarketplaceTransactionConfirmed` to call this.

- [ ] **Step 2: Delete `EnsureOffersNamespaceAsync` and `offersNamespaceEnsured`**
  Remove the method, the field, and the `using PlutoFramework.Model.Xcavate.Profile;` reference if `RealXmarketOfferClient` is no longer used in this file. If `RealXmarketOfferClient` is still used elsewhere in the same project, keep the using and the client class.

- [ ] **Step 3: Build the mobile app**
  Run: `dotnet build XcavateMobileApp/XcavateMobileApp.csproj -f net10.0-android`
  Expected: success.

- [ ] **Step 4: Commit**
  ```bash
  git add realXmarketPlutoFramework/PlutoFramework/Components/XcavateProperty/PropertyDetailViewModel.cs
  git commit -m "feat: remove client-side offers namespace creation from property detail"
  ```

---

## Task 10: Cross-repo integration — wire Indexer to Profile API

**Files:**
- None (configuration only), but verify in each repo.

- [ ] **Step 1: In Indexer deployment config, set `OFFERS_NAMESPACE_CREATED_WEBHOOK_URL`**
  Value: `https://profile-api.xcavate.io/webhooks/offers_namespace_created_devnet?secret=<INDEXER_WEBHOOK_SECRET>`.

- [ ] **Step 2: In Profile API deployment config, ensure `INDEXER_WEBHOOK_SECRET` is set**

- [ ] **Step 3: Verify the existing `PROPERTY_ASSET_REGISTERED_WEBHOOK_URL` still points to `init_property_asset_devnet`**
  The new endpoint depends on the property namespace being created first.

- [ ] **Step 4: Document the wiring in each repo's deployment notes**
  Add one-line notes to `Indexer/docs/deployment.md` and `XcavateProfileApi/docs/deployment.md` if those files exist.

- [ ] **Step 5: Commit (if any docs changed)**
  ```bash
  git add docs/deployment.md
  git commit -m "docs: wire offers_namespace_created webhook between indexer and profile API"
  ```

---

## Task 11: End-to-end verification

- [ ] **Step 1: Run the Profile API test suite**
  Run: `dotnet test tests/XcavateBuckets.Tests/XcavateBuckets.Tests.csproj`
  Expected: all pass.

- [ ] **Step 2: Run the Indexer test suite**
  Run: `cargo test --workspace --locked`
  Expected: all pass.

- [ ] **Step 3: Build the mobile app**
  Run: `dotnet build XcavateMobileApp/XcavateMobileApp.csproj -f net10.0-android`
  Expected: success, no new warnings.

- [ ] **Step 4: Open PRs for each repo**
  - `~/Indexer`: branch `agent/20261017-backend-offers-namespace`
  - `~/XcavateProfileApi`: branch `agent/20261017-backend-offers-namespace`
  - `~/realXmarketMobileApp`: branch `agent/20261017-backend-offers-namespace`

---

## Self-review

- **Spec coverage:** every section of the spec maps to one or more tasks.
- **Step scan:** each step names a single action, a file, and a verification.
- **Type consistency:** `OffersNamespaceCreatedPayload` fields match the Indexer payload produced in Task 7 and the Profile API handler in Task 4.
- **Review focus:** the five failure modes are each covered by tests or build verification.
