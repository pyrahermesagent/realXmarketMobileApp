# Lawyers feature — off-chain services findings (verified 2026-10-14)

Companion to `program-findings.md` (on-chain source of truth). All paths below verified this session unless noted.

## 0. /home/rosta/AGENTS.md

READ (2,201 lines). It is a **ComfyUI CLI skill document** (image/video/3D generation via Comfy). Unrelated to this task — imposes NO constraints on the sibling repos.

## 1. Rent-collector co-signing (fully verified end-to-end)

The mobile app cannot sign with the rent collector key (it lives only in ProfileApi's environment), so the app posts the compiled Solana message to ProfileApi and gets the rent collector's second signature back.

### Mobile client
`/home/rosta/realXmarketMobileApp/realXmarketPlutoFramework/PlutoFramework/Model/Xcavate/Profile/RentCollectorSignatureClient.cs` (91 lines)
- `internal static class RentCollectorSignatureClient`
- `GetRentCollectorSignatureAsync(string investorAddress, byte[] compiledMessage, CancellationToken)` → returns base58-decoded `byte[64]` Ed25519 signature.
- POST `https://profile-api.xcavate.io/api/marketplace/rent-collector-signature`
- Body: `{"message": <base64 of compiled message bytes, NO signatures prefix>}` (one field, lowercase `message`; pinned client 1.0.68 predates the request model, so the app serializes by hand against this known wire shape).
- Header: `X-SS58-Address: <investor base58>` (server checks it; endpoint is unauthenticated by design — the signature is useless without the requester's own signature).
- Non-success → throws with server body. 400/503 never costs a wallet prompt (call happens before tx approval).

### ProfileApi side
- Controller: `/home/rosta/XcavateProfileApi/src/XcavateProfileApi/Controllers/MarketplaceController.cs` line 45 — `[HttpPost("rent-collector-signature")]`, produces 200/400/503.
- Service: `/home/rosta/XcavateProfileApi/src/XcavateProfileApi/Services/MarketplaceRentCollectorSigningService.cs` (406 lines, read in full earlier).
- Request model: `/home/rosta/XcavateProfileApi/src/XcavateProfileApiClient/RentCollectorSignatureRequest.cs` — `{ "message": required string }` (base64).
- Response model: `/home/rosta/XcavateProfileApi/src/XcavateProfileApi/Models/RentCollectorSignatureResponse.cs` — `{ "signature": <base58 64-byte ed25519> }`.

### Service validation rules (verified)
- Validates **legacy (non-v0) messages only**.
- Fee payer of the message MUST be the rent collector pubkey.
- Requester (X-SS58-Address) MUST be a required signer at index ≠ 0 (index 0 is the rent collector fee payer).
- Every instruction's discriminator must be in the allowlist:
  - **Marketplace (6):** Buy, ClaimShares, **ClaimSpvCase**, BuyRelistedShares, Reserve, SendPropertyShares.
  - **Property program (5):** (names in service; see file).
  - **`register_lawyer` / `unregister_lawyer` are NOT in the whitelist → must be added** to `IsAllowedMarketplaceDiscriminator` (exact discriminator bytes in `program-findings.md` table).
- `RequiresRequestingWalletAsFirstAccount` = SendPropertyShares + the 5 property-program discriminators only. `claim_spv_case` is **not** pinned to first-account — pattern: rent collector = fee payer (signer #0), lawyer/investor wallet = signer #1. The Rust program's payer constraint is `address = config.rent_collector`, so register_lawyer/unregister_lawyer fit the same 2-signer pattern once whitelisted.

### Service env config
- `RENT_COLLECTOR_PRIVATE_KEY` — hex, base58, or JSON byte array; parsed to 64 bytes; pubkey = bytes 32..64.
- `RENT_COLLECTOR_MARKETPLACE_PROGRAM_ID` — fallback devnet `dj9Q3CpHvDHwexCbkgJ5APDx4JsTxPssNebkvP15g1T`.
- `RENT_COLLECTOR_PROPERTY_PROGRAM_ID` — fallback `deCp9srk9C6P4BXJaFpjR5H6Jsm6DCq8AL2kk338dVq`.

### Mobile 2-signer cap
`/home/rosta/realXmarketMobileApp/realXmarketPlutoFramework/PlutoFramework/Components/XcavateProperty/XcavateMarketplaceTransactionModel.cs` — `SubmitAsync` hard-caps at 2 signers (user wallet + rent collector). Verified in earlier window; still matches all lawyer-phase instructions (max 2 required signers each — lawyer wallet + rent collector fee payer).

## 2. Settled signer matrix (supersedes earlier draft)

| Instruction | Signers | Path |
|---|---|---|
| register_lawyer | lawyer wallet + rent collector | 2-signer; **whitelist entry missing — add discriminator** |
| unregister_lawyer | lawyer wallet + rent collector | 2-signer; **whitelist entry missing — add discriminator** |
| claim_spv_case | lawyer wallet + rent collector | 2-signer; **already whitelisted** (existing app pattern; program technically allows any wallet as payer, but app uses rent collector for fees) |
| resign_from_case | lawyer wallet only | single-wallet submit |
| confirm_documents | lawyer wallet only | single-wallet |
| assign_developer_lawyer | developer wallet only | single-wallet (developer-side; likely out of mobile scope) |
| vote_on_spv_lawyer | investor wallet only | single-wallet (rent_payer question still open — can investor self-pay?) |
| unlock_voting_shares | investor wallet only | single-wallet (rent_payer question open) |
| finalize_spv_election | any wallet (permissionless) | single-wallet |
| close_candidacy | lawyer wallet (candidacy owner) | single-wallet |
| resolve_silent_verdict | any wallet (permissionless) | single-wallet |
| close_case | per program rules (see program-findings.md) | verify before writing |

## 3. Still unverified (next steps)

**Update 2026-10-17 — lawyer main page shipped** (spec `docs/superpowers/specs/2026-10-17-lawyer-main-page-design.md`,
plan `docs/superpowers/plans/2026-10-17-lawyer-main-page.md`):
- ProfileApi terms-document upload exists: `TermsDocumentController` at
  `api/profiles/{ss58address}/terms-document` (POST signed multipart PDF → Pinata IPFS, CID stored in
  new `LawyerTermsDocuments` table; GET public). First real IPFS integration in the API
  (`Services/Ipfs/`, env `PINATA_JWT` / `IPFS_GATEWAY_URL`, absent key → 503). Uncommitted in
  `/home/rosta/XcavateProfileApi`.
- Indexer lawyer filters exist on the live Rust indexer: `listings(assignedLawyer:, assignableTo:)`
  (PR https://github.com/XcavateBlockchain/Indexer/pull/8, branch `agent/20261017-lawyer-listing-filters`,
  ADR-43). App-side `LawyerProperties` query + `GetLawyerAssignable/AssignedPropertiesAsync` added.
- App: `LawyerMainPage` (+ role-based `//LawyerAccount` routing via `LawyerRoleModel`),
  `TermsDocumentPage` (WebView PDF), hand-rolled signed `TermsDocumentClient`. Uncommitted in app repo
  + `realXmarketPlutoFramework` submodule.
- Deploy order note: indexer PR must merge/deploy before an app release ships the `LawyerProperties` query;
  ProfileApi needs `PINATA_JWT` set before uploads work.

1. ProfileApi `MarketplaceController.cs` lawyer-related endpoints (bucket endpoints?) — grep `lawyer`/`bucket` in `/home/rosta/XcavateProfileApi/src/XcavateProfileApi/Controllers/`.
2. Bucket: `/home/rosta/XcavateProfileApi/tests/XcavateBuckets.Tests/MarketplaceEndpointTests.cs` + `MarketplaceHost.cs`; mobile design doc `docs/superpowers/specs/2026-07-25-bucket-pallet-graphql-api-design.md` (documents_hash source for confirm_documents).
3. Indexer `/home/rosta/xcavate-indexer`: `schema.graphql` ~lines 140–280 (parent type of `ongoingLawyerVotings`/`propertyLawyers`/`userLawyerVotes`); `src/mappings/marketplace.ts` — grep `LawyerRegistered|SpvCaseClaimed|register_lawyer|claim_spv_case`.
4. Mobile GraphQL: does `MarketplaceListings.graphql` (find via Glob `**/MarketplaceListings.graphql`) expose `propertyLawyers`/`ongoingLawyerVotings`/`userLawyerVotes`? Check the 3 existing lawyer query files (grep `lawyer` in app, case-insensitive). New `MarketplaceLawyers.graphql` + StrawberryShake regen likely needed.
5. Quicks: `/home/rosta/xcavate-sumsub-webhook/src/index.ts` (Lawyer level → role grant); `/home/rosta/role-dashboard/app/lib/whitelist/constants.ts` (Lawyer role byte = 3 cross-check); `/home/rosta/AssetDidCommDashboard` README.
6. **`/home/rosta/realxmarket-solana/programs/marketplace/tests/legal.rs` (994 lines)** — verify finalize_spv_election `err!(WrongLawyer)` quirk: winner_registry account required even when quorum NOT met? Check test invocations. Also multi-actor test setup pattern (boss wants reliable automated multi-actor E2E).
7. `/home/rosta/realxmarket-solana/programs/marketplace/tests/lawyers.rs` (272 lines) — register/unregister reference flow.

## 4. Settled decisions (do not reopen)

- Onboarding identical to investor: `UserRoleEnum.Lawyer` → Sumsub level `csharp-verification-lawyer` (`PlutoFramework/Model/Xcavate/XcavateUserModel.cs:25`).
- Legal bucket 100% off-chain (zero "bucket" matches in realxmarket-solana); bucket created when all property tokens reserved (before legal phase); lawyer access granted to BOTH lawyers (developer's + SPV's, must be different people).
- Doc upload is the missing mobile piece; IPFS acceptable.
- Plan date 2026-10-14. Income UI out of scope. Expired-claim handling out of scope (boss's problem).
- App already has: VoteOnSpvLawyer + FinalizeSpvElection encoders, `SpvElectionSection` + `TermsVotingSectionView.xaml` (investor side, ApproveSpvTerms → VoteOnSpvLawyer), PDAs `DeriveLawyer`/`DeriveLawyerCandidacy`/`DeriveLawyerVote` etc.
- ~~NO lawyer-side UI exists today in the app.~~ Superseded 2026-10-17: `LawyerMainPage` + `TermsDocumentPage` + role routing shipped (see §3 update). Claim/resign/confirm-documents UI still does not exist.
