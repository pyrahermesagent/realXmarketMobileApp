# Backend-driven creation of property offers namespaces

## Problem

When an investor taps **Claim property tokens** in the mobile app, the on-chain `claim_shares` transaction is only the first half of the user-visible flow. After that transaction confirms, `PropertyDetailViewModel` calls `RealXmarketOfferClient.CreateOffersNamespaceAsync`, which prompts the user to sign a second GraphQL mutation so the profile API can create the property's `"<property> offers"` namespace. That second prompt is confusing and unnecessary: the namespace is a backend concern, not a user action.

## Goal

Remove the signed namespace-creation call from the mobile app. Instead, the Indexer detects the on-chain events that make a property eligible for secondary-market offers and calls a new unsigned profile-API webhook that creates the namespace automatically.

## Triggers

The offers namespace should exist once a property has live, owned shares that can be offered or relisted. Two on-chain events signal this:

1. `claim_shares` — the property's claim window has opened and the first investor receives shares.
2. `relist_shares` — an existing owner lists shares on the secondary market.

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

The Indexer emits a single `offers_namespace_created` event directly from the `claim_shares` and `relist_shares` mappers. `claim_shares` emits the event alongside the existing `property_claim_started` event; the event id is scoped to the listing PDA so only the first claim per listing fires. `relist_shares` emits a transaction-scoped event so a later delist + relist is not dropped. The profile API's own idempotency prevents duplicate namespaces.

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

For `relist_shares` the payload also includes `share_listing`, `seller`, `amount`, and `share_price`, but the endpoint only needs `listing_id`/`property`/`asset_id`/`cluster` to create the namespace.

**Behavior:**
- Authenticate with `INDEXER_WEBHOOK_SECRET` (same model as `NotificationWebhooksController`: `X-Webhook-Secret` header or `?secret=` query param).
- Look up the property namespace created by `init_property_asset_devnet` using `listing_id` or `property` PDA, matching `WebhooksController.FindNamespaceAsync`.
- Use the property namespace's name (suffixed with `" offers"`) and its creator (the PropertyAsset PDA) as the synthetic caller.
- Call the existing `FindOrCreateOffersNamespaceAsync` logic, idempotent on `(category = "realXmarket-offers", cluster, asset_id)`.
- Return 200 with `namespaceId` on create, or a 200 `ignored` response if the namespace already exists.
- Return 404 if the property namespace is not present yet; the Indexer retry loop will redeliver.
- Use `ProcessedWebhookEvents` for at-least-once idempotency, same as the notification webhooks.

### 2. Indexer — emit the webhook events

Add a new event type:
```rust
pub const OFFERS_NAMESPACE_CREATED: &str = "offers_namespace_created";
```

Emit it in `crates/indexer/src/mapping/marketplace.rs`:
- For `MarketplaceInstruction::ClaimShares(args)`: emit `offers_namespace_created` alongside `property_claim_started`. The event id is `offers_namespace_created:{listing_b58}` so it is deduplicated by listing PDA.
- For `MarketplaceInstruction::RelistShares(args)`: emit `offers_namespace_created` with a transaction-scoped event id (`offers_namespace_created:{tx_signature}:{ctx.index_str}`), because a share listing can be recreated after delisting.

The payload for both events includes `event`, `listing_id`, `listing`, `asset_id`, `cluster`, `slot`, `tx_signature`, `block_time`, and `program`. `claim_shares` also includes `property`; `relist_shares` also includes `share_listing`, `seller`, `amount`, and `share_price`.

In `crates/indexer/src/config.rs::webhook_routes_from_env()`, add:
```rust
("OFFERS_NAMESPACE_CREATED_WEBHOOK_URL", event_type::OFFERS_NAMESPACE_CREATED)
```

The env var points to the new endpoint `https://profile-api.xcavate.io/webhooks/offers_namespace_created_devnet`.

### 3. Mobile app — remove the client-side call

In `PlutoFramework/Components/XcavateProperty/PropertyDetailViewModel.cs`:
- Rename `RefreshListingAndEnsureOffersNamespaceAsync` back to `RefreshListingAsync` or make it call `RefreshListingAsync` only.
- Remove `EnsureOffersNamespaceAsync` and the `offersNamespaceEnsured` flag.
- Keep `OnMarketplaceTransactionConfirmed` refreshing the listing so the UI updates after the claim.
- Remove `RealXmarketOfferClient.CreateOffersNamespaceAsync` usage if it becomes unused; if other flows still call it, keep the client but no longer call it from `PropertyDetailViewModel`.
- Update the code comment to explain that the offers namespace is now backend-driven (Indexer ADR-42).

## Security

- The new endpoint is **not** wallet-signed. It relies on the shared `INDEXER_WEBHOOK_SECRET`.
- The namespace creator is the PropertyAsset PDA (the same synthetic identity used by `init_property_asset_devnet`), not the investor.
- Idempotency prevents duplicate namespaces even if the Indexer redelivers.

## Testing

- **Indexer:** unit tests in `crates/indexer/src/mapping/sibling_tests.rs` for the `offers_namespace_created` payload emitted from both `claim_shares` and `relist_shares`.
- **Profile API:** tests for the new webhook endpoint covering create, redelivery-ignore, missing property namespace (404), and invalid-secret rejection.
- **Mobile app:** build the Android head and verify the claim flow no longer shows a second signing prompt.

## Documentation

- Add an ADR to `Indexer/DECISIONS.md` describing the new webhook event and the rationale (ADR-42).
- Update `Indexer/RUNBOOK.md` webhook setup section.
- Update mobile-app code comments to explain that offers namespace creation is now backend-driven.

## Out of scope

- Changing the GraphQL `createOffersNamespace` mutation. It remains available for backward compatibility and manual/admin use, but the mobile app stops using it for this flow.
- Creating the offers namespace at property registration time. The property namespace is created then, but the offers namespace is intentionally tied to claim/relist so it only exists for properties that have reached the secondary-market stage.
