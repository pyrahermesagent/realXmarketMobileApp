# Backend-driven creation of property offers namespaces

## Problem

When an investor taps **Claim property tokens** in the mobile app, the on-chain `claim_shares` transaction is only the first half of the user-visible flow. After that transaction confirms, `PropertyDetailViewModel` calls `RealXmarketOfferClient.CreateOffersNamespaceAsync`, which prompts the user to sign a second GraphQL mutation so the profile API can create the property's `"<property> offers"` namespace. That second prompt is confusing and unnecessary: the namespace is a backend concern, not a user action.

## Goal

Remove the signed namespace-creation call from the mobile app. Instead, the Indexer detects the on-chain events that make a property eligible for secondary-market offers and calls a new unsigned profile-API webhook that creates the namespace automatically.

## Triggers

The offers namespace should exist once a property has live, owned shares that can be offered or relisted. Two on-chain events signal this:

1. `claim_shares` — the property's claim window has opened and the first investor receives shares.
2. `relist_shares` — an existing owner lists shares on the secondary market.

The Indexer already emits `property_claim_started` for the first successful `claim_shares` per listing. It currently emits nothing for `relist_shares`.

## Architecture

```
┌─────────────────┐      claim_shares / relist_shares       ┌─────────────┐
│  Solana devnet  │ ───────────────────────────────────────▶ │   Indexer   │
└─────────────────┘                                          └──────┬──────┘
                                                                     │ webhook
                                                                     │ (signed with
                                                                     │  INDEXER_WEBHOOK_SECRET)
                                                                     ▼
                                                          ┌─────────────────────┐
                                                          │  XcavateProfileApi  │
                                                          │ POST /webhooks/     │
                                                          │   offers_namespace_ │
                                                          │   created_devnet    │
                                                          └─────────────────────┘
```

## Changes

### 1. XcavateProfileApi — new webhook endpoint

Add `POST /webhooks/offers_namespace_created_devnet` to `WebhooksController`.

**Request body (JSON):**
```json
{
  "event": "offers_namespace_created",
  "listing_id": 123,
  "listing": "<listing-pda-base58>",
  "property": "<property-pda-base58>",
  "asset_id": 123,
  "cluster": "devnet",
  "tx_signature": "<tx-sig-base58>",
  "slot": 505123456,
  "block_time": "2026-10-17T12:00:00Z"
}
```

**Behavior:**
- Authenticate with `INDEXER_WEBHOOK_SECRET` (same model as `NotificationWebhooksController`: `X-Webhook-Secret` header or `?secret=` query param).
- Look up the property namespace created by `init_property_asset_devnet` using `listing_id` or `property` PDA, matching `WebhooksController.FindNamespaceAsync`.
- Use the property namespace's name (suffixed with `" offers"`) and its creator (the PropertyAsset PDA) as the synthetic caller.
- Call the existing `FindOrCreateOffersNamespaceAsync` logic, idempotent on `(category = "realXmarket-offers", cluster, asset_id)`.
- Return 200 with `namespaceId` on create, or a 200 `ignored` response if the namespace already exists.
- Return 404 if the property namespace is not present yet; the Indexer retry loop will redeliver.
- Use `ProcessedWebhookEvents` for at-least-once idempotency, same as the notification webhooks.

### 2. Indexer — emit the webhook events

#### 2.1 Extend `property_claim_started`

In `crates/indexer/src/mapping/marketplace.rs`, add `asset_id` and `cluster` to the existing `property_claim_started` payload. `asset_id` is available from the instruction accounts (`property` PDA is seeded `["property", listing_id]`, and the listing row already stores the asset id; the simplest source is the decoded instruction context or a lookup). The `cluster` is the indexer's configured cluster.

The event remains deduplicated by listing PDA so only the first claim fires.

#### 2.2 Add `property_share_listing_created`

Add a new event type:
```rust
pub const PROPERTY_SHARE_LISTING_CREATED: &str = "property_share_listing_created";
```

Emit it in `map_instruction` for `MarketplaceInstruction::RelistShares(args)`:
- `event_id`: transaction-scoped (`property_share_listing_created:{tx_signature}:{ctx.index_str}`), because a share listing can be recreated after delisting.
- Payload includes `event`, `asset_id`, `listing_id`, `share_listing` (PDA), `seller`, `amount`, `share_price`, `cluster`, `slot`, `tx_signature`, `block_time`, `program`.

#### 2.3 Route both events to the profile API

In `crates/indexer/src/config.rs::webhook_routes_from_env()`, add:
```rust
("PROPERTY_CLAIM_STARTED_WEBHOOK_URL", event_type::PROPERTY_CLAIM_STARTED)
("PROPERTY_SHARE_LISTING_CREATED_WEBHOOK_URL", event_type::PROPERTY_SHARE_LISTING_CREATED)
```

Both env vars point to the same new endpoint `https://profile-api.xcavate.io/webhooks/offers_namespace_created_devnet`.

### 3. Mobile app — remove the client-side call

In `PlutoFramework/Components/XcavateProperty/PropertyDetailViewModel.cs`:
- Rename `RefreshListingAndEnsureOffersNamespaceAsync` back to `RefreshListingAsync` or make it call `RefreshListingAsync` only.
- Remove `EnsureOffersNamespaceAsync` and the `offersNamespaceEnsured` flag.
- Keep `OnMarketplaceTransactionConfirmed` refreshing the listing so the UI updates after the claim.
- Remove `RealXmarketOfferClient.CreateOffersNamespaceAsync` usage if it becomes unused; if other flows still call it, keep the client but no longer call it from `PropertyDetailViewModel`.

## Security

- The new endpoint is **not** wallet-signed. It relies on the shared `INDEXER_WEBHOOK_SECRET`.
- The namespace creator is the PropertyAsset PDA (the same synthetic identity used by `init_property_asset_devnet`), not the investor.
- Idempotency prevents duplicate namespaces even if the Indexer redelivers.

## Testing

- **Indexer:** unit tests in `crates/indexer/src/mapping/sibling_tests.rs` for the extended `property_claim_started` payload and the new `property_share_listing_created` event.
- **Profile API:** tests for the new webhook endpoint covering create, redelivery-ignore, missing property namespace (404), and invalid-secret rejection.
- **Mobile app:** build the Android head and verify the claim flow no longer shows a second signing prompt.

## Documentation

- Add an ADR to `Indexer/DECISIONS.md` describing the new webhook event and the rationale.
- Update `Indexer/RUNBOOK.md` webhook setup section.
- Update mobile-app code comments to explain that offers namespace creation is now backend-driven.

## Out of scope

- Changing the GraphQL `createOffersNamespace` mutation. It remains available for backward compatibility and manual/admin use, but the mobile app stops using it for this flow.
- Creating the offers namespace at property registration time. The property namespace is created then, but the offers namespace is intentionally tied to claim/relist so it only exists for properties that have reached the secondary-market stage.
