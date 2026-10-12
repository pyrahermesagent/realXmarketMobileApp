# realXmarket Mobile — Full Solana End-to-End Coverage: Gap Analysis & Roadmap

> **For agentic workers:** This is a gap-analysis and sequencing roadmap, not a single implementable plan. It covers multiple independent subsystems; per the writing-plans scope check, each phase below should get its own spec (`docs/superpowers/specs/`) and task-level plan before implementation, following this repo's established spec→plan cycle. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Take the app from 19/95 on-chain instructions reachable (12 from UI) to full end-to-end coverage of the four realxmarket-solana programs described in the architecture diagram (mermaid-diagram-1791757245448).

**Architecture:** .NET MAUI app (XcavateMobileApp) on the PlutoFramework submodule. On-chain access is hand-rolled Anchor instruction builders in `PlutoFrameworkCore/Xcavate/` (discriminators transcribed from `idls/devnet/*.json`, pinned by `PlutoFrameworkTests`), state-resolving wrappers in `*CallsModel.cs`, reads via the devnet GraphQL indexer (StrawberryShake, `Indexers/XcavateDevnetIndexer/Queries/*.graphql`), submission via `XcavateMarketplaceTransactionModel.SubmitAsync` (1–2 signers, rent-collector co-sign through the profile API).

**Tech Stack:** .NET MAUI, Solnet, Anchor IDLs, GraphQL indexer (`indexer-devnet.xcavate.io`), profile API (`profile-api.xcavate.io`), MSTest (`PlutoFrameworkTests`).

**Spec:** the attached mermaid diagram (4 programs, 95 instructions, listing/SPV/governance/income state flows) + `idls/devnet/{marketplace,property,regions,xcavate_whitelist}.json`. IDLs are the source of truth for discriminators, account order and PDA seeds.

## Current coverage (verified 2026-10-12)

| Program | On-chain fns | Builders exist | Wired to UI | Missing |
|---|---|---|---|---|
| marketplace | 45 | 12 | 12 | 33 |
| property | 25 | 7 | 3 | 18 |
| regions | 16 | 0 | 0 | 16 (no client at all) |
| xcavate-whitelist | 9 | 0 | 0 (read-only via indexer + roles.xcavate.io WebView) | 9 |
| **Total** | **95** | **19** | **15** | **76** |

## Global Constraints

- Builders are hand-rolled static classes (`XcavateMarketplaceProgram.cs`, `XcavatePropertyProgram.cs`); discriminators are hardcoded byte arrays that tests re-derive via `sha256("global:<name>")[0..8]` — every new builder MUST get the same discriminator/account-order/PDA-seed test treatment in `PlutoFrameworkTests`.
- Writable flags deliberately do NOT follow the IDL: the deployed binary requires every account writable (`XcavateMarketplaceProgram.cs:18-22`). New builders must mark all metas writable.
- Submission pipeline supports at most 2 signers (investor + rent-collector co-sign via profile API); >2 signers is refused up front. Instructions needing admin/lawyer/developer signatures need a signer-flow decision (single-signer tx or new co-sign endpoint).
- Reads are indexer-first; direct RPC Borsh parsing exists only for Config and ShareListing (`AnchorBorshReader`). Prefer new `.graphql` queries over new RPC parsers; the indexer schema already exposes (unused) `offers`, `reservations`, `lawyers`, `lawyerVotes`, `propertyIncomes`, `incomeCheckpoints`, `lettingAgents`, `resignationNotices`, and all regions types.
- IDLs are embedded resources used ONLY for error decoding (`AnchorIdlErrorCatalog`); do not introduce IDL-driven codegen — match the existing transcription pattern.
- `idls/mainnet/` is empty and mainnet addresses/indexer URL are `null` (`XcavateDeploymentModel` gates to devnet). Nothing ships to mainnet in this roadmap.
- Offers are deliberately off-chain (profile-API buckets + realxmessenger WebView, settling via `buy_relisted_shares`); on-chain `make_offer`/`accept_offer`/`reject_offer`/`cancel_offer` stay unused unless product decides otherwise (Phase 8 decision, not a default task).
- UI must follow `docs/ui-guidelines/01-csharp-xamarin-maui-ui-ux-practices.md` (design tokens, page/popup patterns, `SolanaTransactionPopup` status flow).
- Framework code lives in the `realXmarketPlutoFramework` git submodule; `PlutoFrameworkTests` reaches `PlutoFrameworkCore` only — UI ViewModels are verified by build + manual run, Core logic by unit tests.

## Review Focus

1. **Shares locked by voting can never be unlocked in-app today** — `unlock_proposal_votes`/`unlock_challenge_votes`/`unlock_agent_votes`/`unlock_voting_shares` builders exist or are trivial, but no caller: any user who votes permanently loses voting power and can't transfer those shares. Fixed by Phase 0; test: cast a vote on devnet, then unlock from the UI.
2. **Revotes may fail silently** — vote wrappers always pass `previousCandidate = null` (`XcavateMarketplaceCallsModel.cs:169`, `XcavatePropertyCallsModel.cs:89`); changing an existing vote likely needs the previous candidate account. Test: vote for candidate A then candidate B on devnet.
3. **`buy_relisted_shares` CPI-settles income first** — any share transfer without the settle_income checkpoint path mis-pays rent; `send_property_shares` (Phase 7) must replicate the 29-account CPI pattern, not a plain SPL transfer.
4. **Multi-signer admin/lawyer instructions** — `execute_deal`, `assign_developer_lawyer`, config updates etc. assume signer combinations the pipeline refuses; each phase must state its signer story before building UI.
5. **Indexer lag vs. claim windows** — new reads (reservations, incomes, elections) are time-sensitive (claim window, "SPV silent in last 20%"); indexer-only reads can show stale state at boundaries. Each phase's tests must cover the expired/edge state, not just the happy path.

---

### Task 0: Unwire-blockers — expose the builders that already exist

**Files:**
- Modify: `realXmarketPlutoFramework/PlutoFrameworkCore/Xcavate/XcavatePropertyCallsModel.cs` (add wrappers for `FinalizeProposal`, `UnlockProposalVotes`, `UnlockChallengeVotes`, `UnlockAgentVotes` — builders at `XcavatePropertyProgram.cs:239,267,295,324`)
- Modify: `realXmarketPlutoFramework/PlutoFramework/Components/XcavateProperty/PropertyDetailViewModel.cs` (vote sections: unlock + agent-vote entry points)
- Modify: `realXmarketPlutoFramework/PlutoFramework/Components/XcavateProperty/GovernanceVotingSectionView*.xaml` (or new `MyVotesSectionView`)
- Test: `realXmarketPlutoFramework/PlutoFrameworkTests/XcavatePropertyProgramTests.cs` (wrapper-level account resolution)

**Interfaces:**
- Consumes: existing builders; `XcavateMarketplaceIndexerModel.GetGovernanceStateAsync` (already returns caller's locks/votes).
- Produces: `Task<string> UnlockProposalVotesAsync(...)`, `UnlockChallengeVotesAsync(...)`, `UnlockAgentVotesAsync(...)` on `XcavatePropertyCallsModel`; UI entry points on the property detail page.

- [ ] **Step 1:** Failing test: wrapper resolves the vote PDA + share-lock accounts for a recorded devnet vote (mirror existing wrapper tests).
- [ ] **Step 2:** Implement the four wrappers (same pattern as `VoteOnProposalAsync`).
- [ ] **Step 3:** `dotnet test PlutoFrameworkTests` green.
- [ ] **Step 4:** Add "Unlock shares" / "Vote for agent" UI to the governance section per ui-guidelines; submit via `XcavateMarketplaceTransactionModel.SubmitAsync`.
- [ ] **Step 5:** Manual devnet pass: vote → locked visible → unlock → listed balance restored. Commit.

**Why first:** it fixes a live user-facing lock-up using code that already exists and is tested at builder level — cheapest possible win, and it de-risks every later governance phase.

---

### Task 1: Rent / income lifecycle (property program)

Missing: `distribute_income`, `claim_income`, `close_income_checkpoint` (+ read surface for `propertyIncomes`, `incomeCheckpoints`).

**Files:**
- Modify: `XcavatePropertyProgram.cs` (3 builders + income/checkpoint PDA derivations — seeds already mirrored at `XcavateMarketplaceProgram.cs:160-169`)
- Modify: `XcavatePropertyCallsModel.cs`
- Create: `Indexers/XcavateDevnetIndexer/Queries/PropertyIncomes.graphql` (schema types exist, unused)
- Modify: `XcavateMarketplaceIndexerModel.cs` (`GetIncomeStateAsync`)
- Modify: `Pages/InvestorMainPageViewModel.cs` / portfolio section ("Claim rent" per property, claimable total)
- Test: `XcavatePropertyProgramTests.cs`

**Interfaces:**
- Produces: builders `DistributeIncome`, `ClaimIncome`, `CloseIncomeCheckpoint`; wrapper `Task<string> ClaimIncomeAsync(...)`; indexer `GetIncomeStateAsync(propertyId)` → checkpoints + claimable amounts.

- [ ] **Step 1:** Failing discriminator/account-order tests for the 3 builders, transcribed from `idls/devnet/property.json`.
- [ ] **Step 2:** Implement builders (all metas writable) until tests pass.
- [ ] **Step 3:** Indexer query + model; unit-test parsing against a recorded response.
- [ ] **Step 4:** Investor UI: claimable-rent row on portfolio + property detail; submit flow. Letting-agent `distribute_income` entry point is role-gated (`LettingAgent` role via `WhitelistModel`).
- [ ] **Step 5:** Commit; manual devnet: distribute → checkpoint visible → claim → token balance increases.

**Why second:** rent is the product's core promise to investors; today the app can sell shares but can never pay out. Everything it needs (PDA seeds, role gating, indexer schema) already exists in embryo.

---

### Task 2: Governance lifecycle completion (property program)

Missing: `propose`, `challenge_agent`, `finalize_proposal`, `finalize_challenge`, `finalize_agent_election`, `close_agent_candidacy`, `add_letting_agent`, `remove_letting_agent`, `claim_property`, `resign`, `finalize_resignation`, `settle_income` (direct builder; today only reachable via CPI).

**Files:**
- Modify: `XcavatePropertyProgram.cs`, `XcavatePropertyCallsModel.cs`
- Modify: `Indexers/.../PropertyGovernance.graphql` (extend with candidacies/resignationNotices/lettingAgents — schema exists)
- Create: `PlutoFramework/Components/XcavateProperty/Proposals/CreateProposalViewModel.cs` (+ view), `ChallengeAgentViewModel.cs`, agent-election section on the detail page
- Test: `XcavatePropertyProgramTests.cs`

- [ ] **Step 1:** Failing builder tests (discriminators, account orders, PDA seeds from IDL).
- [ ] **Step 2:** Implement 12 builders + wrappers.
- [ ] **Step 3:** Indexer reads for agent elections + proposals lifecycle states.
- [ ] **Step 4:** UI: letting-agent "New proposal", holder "Challenge agent", candidacy list + `vote_on_agent` wiring (builder exists!), finalize buttons gated on vote-end timestamps.
- [ ] **Step 5:** Commit; devnet walkthrough of full proposal→vote→finalize→unlock loop.

**Why third:** completes the letting/holder-governance triangle the diagram shows for the property program; depends on Task 0's unlock path and Task 1's checkpoint model (proposals settle income).

---

### Task 3: SPV & lawyer legal process (marketplace program)

Missing: `register_lawyer`, `unregister_lawyer`, `claim_spv_case`, `finalize_spv_election`, `close_candidacy`, `unlock_voting_shares`, `lawyer_confirm_documents`, `resign_from_case`, `execute_deal`, `resolve_silent_verdict`, `withdraw_deposit_unsold`, `settle_cancelled_fees`, `close_case`, `close_dead_listing`, `assign_developer_lawyer`, `withdraw_cancelled`/`settle` edge states per diagram ("both agree / both reject-split / SPV silent in last 20% / teardown").

**Files:**
- Modify: `XcavateMarketplaceProgram.cs`, `XcavateMarketplaceCallsModel.cs`
- Create: `Indexers/.../MarketplaceLegalProcess.graphql` (lawyers, lawyerVotes, spv case state)
- Create: `PlutoFramework/Components/XcavateLegal/` — lawyer persona pages: case inbox (`claim_spv_case`), document checklist (`lawyer_confirm_documents`), `execute_deal` confirmation; investor-facing legal-status section on the detail page
- Test: `XcavateMarketplaceProgramTests.cs`

**Interfaces:**
- Produces: `GetLegalCaseStateAsync(listingId)`; lawyer role gate (`Lawyer` via `WhitelistModel`); signer story: lawyer txs are single-signer (no rent collector) — confirm against deployed binary before UI work.

- [ ] **Step 1:** Failing builder tests incl. lawyer/vote PDA seeds (`lawyer-candidate`, `lawyer-vote` — already derived for `vote_on_spv_lawyer`).
- [ ] **Step 2:** Implement 15 builders + wrappers.
- [ ] **Step 3:** Indexer legal-case query + model tests.
- [ ] **Step 4:** Lawyer UI (new persona section reachable from `MainMenuPage` when role present); investor read-only status.
- [ ] **Step 5:** Commit; devnet E2E: sold-out listing → create_spv → lawyer election → claim case → confirm docs → execute_deal → investors' shares final.

**Why fourth:** without `execute_deal`, no primary-market purchase ever legally settles — the current investor flow dead-ends after the SPV vote. This is the largest persona gap (lawyer has zero UI).

---

### Task 4: Developer property onboarding (marketplace program)

Missing: `list_property`, `init_property_assets`, `upgrade_object` (+ document upload wiring to existing `S3Model`).

**Files:**
- Modify: `XcavateMarketplaceProgram.cs`, `XcavateMarketplaceCallsModel.cs`
- Revive/rewrite: dormant `ModifyPropertyPage`/`ModifyPropertyViewModel`, `CompanyPage` (currently mock data) → `Components/XcavateDeveloper/`
- Test: `XcavateMarketplaceProgramTests.cs`

- [ ] **Step 1:** Failing builder tests (incl. `PendingAssets` state accounts).
- [ ] **Step 2:** Implement 3 builders + wrappers.
- [ ] **Step 3:** Developer UI: create listing wizard (details → docs/images via S3 presigned URLs → submit `list_property` → `init_property_assets`), role-gated `Developer`.
- [ ] **Step 4:** Commit; devnet: developer lists property → appears in marketplace feed via indexer.

**Why fifth:** today every listing must be seeded out-of-band; the diagram's "PendingAssets → Listed" flow starts here. Depends on nothing later, but listed properties exercise Tasks 1–3.

---

### Task 5: Regions program — client from scratch

Missing: all 16 (`propose_new_region`, `vote_on_region_proposal`, `finalize_region_proposal`, `unlock_voting_token`, `create_region`, `clear_region_state`, `claim_open_region`, `initiate_resignation`, `create_new_location`, `remove_location`, `adjust_listing_duration`, `adjust_region_tax` + 4 config/authority).

**Files:**
- Create: `PlutoFrameworkCore/Xcavate/XcavateRegionsProgram.cs` (+ `RegionsCallsModel.cs`, `RegionsIndexerModel.cs`)
- Create: `Indexers/.../Regions.graphql` (schema types `regions`, `regionProposals`, `locations`, `regionStates`, `voteRecords` exist unused)
- Create: `PlutoFramework/Components/XcavateRegions/` (RegionalOperator + proposal voting UI)
- Test: `XcavateRegionsProgramTests.cs` (new, same discriminator pattern)
- Delete: dead legacy `RegionModel.cs` (Substrate-era, returns null) once replaced.

- [ ] **Step 1:** Failing builder tests from `idls/devnet/regions.json` (program address already at `XcavateProgramAddresses.cs:34`).
- [ ] **Step 2:** Implement client + indexer model.
- [ ] **Step 3:** UI: region list/detail, operator claim/resign, proposal voting; surface region `tax_bps`/listing-duration/postcode-validity reads in the marketplace filter UI.
- [ ] **Step 4:** Commit; devnet: propose → vote → finalize → region claimable → operator claims → location appears in filters.

**Why sixth:** regions gate listing parameters (tax, duration, postcodes) per the diagram, but nothing in the current investor loop blocks on them; building the client from scratch benefits from the patterns hardened in Tasks 1–4.

---

### Task 6: Whitelist program — native role admin (or explicit WebView delegation)

Missing: all 9 instructions (`assign_role`, `remove_role`, `set_permission`, `add_admin`, `remove_admin`, `renounce_role` + config/authority trio).

**Files:**
- Create: `PlutoFrameworkCore/Xcavate/XcavateWhitelistProgram.cs` (+ calls model); `RoleAccount` PDA derivation already exists (`XcavateMarketplaceProgram.cs:127`).
- Decision point: admin functions may stay in the `roles.xcavate.io` WebView dApp; if so, ship only user-facing `renounce_role` natively and document the delegation in the spec.
- Test: `XcavateWhitelistProgramTests.cs`

- [ ] **Step 1:** Spec decision (native admin vs WebView delegation) — record in `docs/superpowers/specs/`.
- [ ] **Step 2:** Failing builder tests → implement builders → green.
- [ ] **Step 3:** UI per decision. Commit.

**Why late:** role *reads* already work via indexer and role *management* has a working WebView; the marginal value is native UX, not unblocking flows.

---

### Task 7: Marketplace housekeeping & peer transfer

Missing: `send_property_shares` (must CPI `settle_income` like `buy_relisted_shares`), standalone `delist_shares` UI, `close_share_holding`, `close_reservation`, `release_reservation`, `close_cancelled_position`, `lock_shares`/`unlock_shares` (native, outside vote CPIs).

**Files:** `XcavateMarketplaceProgram.cs`, `XcavateMarketplaceCallsModel.cs`, `Components/Solana/Transfer/` (send-shares branch) or property detail "Send" action; tests as before.

- [ ] **Step 1:** Failing builder tests, esp. the 29-account CPI mirror for `send_property_shares`.
- [ ] **Step 2:** Implement + wrappers.
- [ ] **Step 3:** UI: send shares to address, delist my listing, close empty positions (rent reclaim).
- [ ] **Step 4:** Commit; devnet: send shares between two wallets → income checkpoints settle correctly for both.

---

### Task 8: On-chain offers — decision only

The program's `make_offer`/`accept_offer`/`reject_offer`/`cancel_offer` duplicate the shipped off-chain offer protocol (`PlutoFrameworkCore/Xcavate/Offers/OfferProtocol.cs`, messenger WebView settlement). **Default: keep off-chain.** Write a short spec recording the decision and the trigger conditions that would flip it (e.g. need for escrowed on-chain offers, `lock_shares` during negotiation). Only if flipped: builders + `Offers.graphql` + native negotiation UI as its own plan.

---

### Task 9: Admin/config surface (all 4 programs × initialize/update_config, update/accept_authority = 16 fns)

These are one-off/deployment operations with multi-signer questions the mobile pipeline refuses today. Recommendation: **do not put them in the mobile app** — cover with a CLI/web console (or Anchor scripts in realxmarket-solana) and record the delegation in a spec. Mobile ships only if product requires in-app admin.

---

### Task 10: Mainnet readiness gate

- Populate `idls/mainnet/`, fill mainnet addresses in `XcavateProgramAddresses.cs` and indexer URL (`XcavateDeploymentModel`), run the 2026-09-27 network-switch plan's degradation matrix against real mainnet programs. Blocked on program deployment outside this repo; keep as the final gate after Phases 0–7 land on devnet.

---

## Self-Review notes

- **Spec coverage:** all 95 diagram instructions map to exactly one task (76 missing + 19 existing noted in coverage table); the diagram's flows (claim window, SPV silent-verdict, teardown, lock/settle CPIs) map to Tasks 3, 7 and Review Focus items.
- **Order rationale:** fix lock-ups (0) → money out to investors (1) → governance (2) → legal settlement (3) → supply side (4) → new programs (5, 6) → polish (7) → decisions/gates (8–10). Each phase is independently shippable and follows the repo's spec→plan→TDD cycle.
- **Type consistency:** every task names the exact existing files/patterns it extends; new files follow the `Xcavate<Program>Program.cs` / `*CallsModel.cs` / `*.graphql` naming already in the repo.
