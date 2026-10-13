# Investor income, share transfer & finalize cranks

> Task-level implementation plan, derived from the gap analysis
> `docs/superpowers/plans/2026-10-12-solana-e2e-gap-roadmap.md` (Tasks 1, 2, 3, 7
> there). Scope: the investor-facing missing instructions — `claim_income`,
> `close_income_checkpoint`, `vote_on_agent` UI, `send_property_shares`,
> `close_share_holding` — plus the permissionless finalize cranks
> (`finalize_proposal`, `finalize_challenge`, `finalize_agent_election`,
> `finalize_spv_election`, `resolve_silent_verdict`, `execute_deal`).
> Cross-repo follow-through in `~/Indexer`, `~/XcavateProfileApi`,
> `~/AssetDidCommDashboard`. UX is the priority: everything must feel like the
> rest of the app (`docs/ui-guidelines/01-csharp-xamarin-maui-ui-ux-practices.md`).

**Decided fork:** in-app contextual cranks (not a backend crank service) — zero
new infra, banners appear exactly where users already look. All six finalize
instructions are single-signer; if the app doesn't crank, nobody does.

## Global Constraints

(inherit the roadmap's Global Constraints verbatim; the binding ones here:)

- Builders are hand-rolled static classes (`XcavateMarketplaceProgram.cs`,
  `XcavatePropertyProgram.cs`); discriminators are hardcoded byte arrays that
  tests re-derive via `SHA256("global:<name>")[0..8]` — every new builder MUST
  get discriminator + account-order + arg-encoding tests in
  `PlutoFrameworkTests` (pattern: `XcavatePropertyProgramTests.cs:31-55`,
  `:132-149`).
- Every account meta is deliberately writable regardless of the IDL
  (`XcavateMarketplaceProgram.cs:18-22` class doc).
- All IDs/u128 accumulators come from the indexer as decimal strings; parse
  with `XcavateMarketplaceIndexerModel.ParseBigInteger`
  (`XcavateMarketplaceIndexerModel.cs:819-826`). `per_share` math uses
  `BigInteger` (u128).
- Submission goes through
  `PlutoFramework/Components/XcavateProperty/XcavateMarketplaceTransactionModel.SubmitAsync`:
  2 signers max; payer pinned to `config.RentCollector` for user ops that
  create accounts; cranks are single-signer (cranker only) and need no co-sign.
- Reads are indexer-first. New queries follow the explicit `first:`/`offset:`
  convention (`Indexers/XcavateDevnetIndexer/Queries/*.graphql`); "u128 as
  decimal string" JSON fields (`streams`, `entries`) are parsed client-side.
- IDLs at `idls/devnet/{marketplace,property}.json` are the source of truth for
  discriminators, account order, PDA seeds. No IDL-driven codegen.
- UI follows `docs/ui-guidelines/01-csharp-xamarin-maui-ui-ux-practices.md`:
  `BottomPopupCard` sheets, mandatory button grid patterns (§3.10:338-402),
  `FormInputView` for amount/address entry, `SolanaTransactionPopupViewModel`
  status flow, `ISetToDefault` + one-shot `SubmissionFailed` re-open handler
  (pattern: `SendSharesPopupViewModel.cs:388-407`).
- Framework code lives in the `realXmarketPlutoFramework` git submodule on
  branch `feat/investor-income-cranks`; the parent-repo submodule pointer bumps
  at the end. Core logic verified by `PlutoFrameworkTests`; ViewModels verified
  by full build (`XcavateMobileApp.Android` head).
- Devnet only (`XcavateDeploymentModel` gates mainnet).

## Verified baseline (2026-10-13)

Already in the submodule working tree (uncommitted):
- `XcavateMarketplaceProgram.cs` builders: `SendPropertyShares` (21 accounts,
  settle-income CPI tail replicated from `BuyRelistedShares`),
  `CloseShareHolding`, `FinalizeSpvElection`, `ResolveSilentVerdict`,
  `ExecuteDeal` + PDAs `DeriveLawyer`, `DeriveVault`, `DeriveRegion`; encoders
  `U16/U32/U64` at `:891-923`.
- Property builders existing: `VoteOnAgent` (has `previousCandidate` param,
  `XcavatePropertyProgram.cs:199-232`), `FinalizeProposal` (`:239-259`),
  `UnlockProposalVotes`/`UnlockChallengeVotes`/`UnlockAgentVotes` (`:267-346` —
  no callers).
- GraphQL (untracked): `PropertyIncomes.graphql`, `IncomeCheckpoints.graphql`,
  `PropertyAgentElection.graphql` — codegen at build time.
- `SendSharesPopupViewModel.cs` (untracked, calls the MISSING
  `XcavateMarketplaceCallsModel.SendPropertySharesAsync` — tree does not
  compile until Task 3).
- `SolanaTransactionPopupView.xaml`: button text "View in Solana Explorer" →
  "View details" (keep; commit with Task 1).

Gaps (details in tasks): property builders `ClaimIncome`,
`CloseIncomeCheckpoint`, `FinalizeChallenge`, `FinalizeAgentElection`,
`DeriveIncomeVault`; all property wrappers; `SendPropertySharesAsync` +
crank wrappers; `VoteOnAgentAsync`/`VoteOnSpvLawyerAsync` hardcode
`previousCandidate: null` (`XcavatePropertyCallsModel.cs:111`,
`XcavateMarketplaceCallsModel.cs:169`); indexer reader methods + claimable
math; all UI; tests for everything above; cross-repo co-sign allowlist +
pushes + DIDComm `previous_candidacy` parity.

Signer story (checked against the IDLs): all six finalize cranks +
`close_share_holding` + `close_income_checkpoint` + `unlock_*` are
single-signer. `send_property_shares` and `claim_income` are 2-signer
(± rent-collector as payer) → profile-API co-sign allowlist must be extended
to the property program before they can ship (Task 9).

## Task 1: Marketplace builder tests + working-tree consolidation

**Files:**
- Modify: `realXmarketPlutoFramework/PlutoFrameworkTests/XcavateMarketplaceProgramTests.cs`
  (add discriminator/account-shape/encoding tests for `SendPropertyShares`,
  `CloseShareHolding`, `FinalizeSpvElection`, `ResolveSilentVerdict`,
  `ExecuteDeal`, following the existing `:27-77` pattern)
- Carry the uncommitted builders + popup text change forward into commits.

**IDL truth (verbatim, from `idls/devnet/marketplace.json`):**
- `send_property_shares(asset_id:u64, amount:u32)` disc `[214,10,132,137,120,190,89,240]`,
  21 accts: [0] sender(signer) [1] payer(w,signer) [2] sender_role [3] receiver
  [4] receiver_role [5] listing [6] property(w) [7] sender_holding(w) ["share",asset_id,sender]
  [8] receiver_holding(w) [9] share_mint ["share-mint",asset_id] [10] mint_auth
  ["mint-auth",asset_id] [11] sender_share_account(w) [12] receiver_share_account(w)
  [13] cpi_auth ["cpi-auth"] [14] income [15] sender_checkpoint(w)
  [16] receiver_checkpoint(w) [17] property_program [18] share_token_program
  [19] associated_token_program [20] system_program.
- `close_share_holding()` disc `[184,83,50,153,169,129,37,176]`, 5 accts:
  [0] cranker(signer) [1] config [2] rent_collector(w) [3] property(w)
  [4] holding(w).
- `finalize_spv_election(listing_id)` disc `[2,249,39,73,224,169,145,100]`, 4 accts:
  [0] cranker(signer) [1] listing(w) [2] property [3] winner_registry(w, optional; None
  encodes as the marketplace program id).
- `resolve_silent_verdict(listing_id)` disc `[229,121,58,51,193,189,164,60]`, 2 accts.
- `execute_deal(listing_id)` disc `[107,92,176,235,53,80,14,94]`, 13 accts:
  [0] cranker(signer) [1] config [2] listing(w) [3] property(w) [4] region
  [5] developer_lawyer_registry(w) [6] spv_lawyer_registry(w) [7] listing_vault
  [8] xcav_mint [9] developer_token(w) [10] vault(w) [11] token_program
  [12] payment_token_program.

- [ ] Test-first additions to the discriminator table + account-shape + arg
  encoding tests for the 5 builders (a PDA-position assertion per derived
  account, per the existing pattern). All metas writable is the repo rule even
  where the IDL says otherwise.
- [ ] Fix any builder/account mismatches the tests expose.
- [ ] `dotnet test PlutoFrameworkTests` green.
- [ ] Commit builders+tests; separate commit for the popup text change.

## Task 2: Property builders — claim income & finalize cranks

**Files:**
- Modify: `realXmarketPlutoFramework/PlutoFrameworkCore/Xcavate/XcavatePropertyProgram.cs`
  (`ClaimIncome`, `CloseIncomeCheckpoint`, `FinalizeChallenge`,
  `FinalizeAgentElection`; `DeriveIncomeVault` seed `"income-vault"`
  [u8x6:"income",asset_id]-style derivation mirroring `DeriveIncome` on the
  marketplace side; re-use existing `DeriveIncome`/`DeriveIncomeCheckpoint`
  owners — note these live on the MARKETPLACE program deriving under
  `programs.Property`; move or mirror so the property program exposes what its
  own instructions need, no duplication of seeds)
- Modify: `realXmarketPlutoFramework/PlutoFrameworkTests/XcavatePropertyProgramTests.cs`

**IDL truth (verbatim, from `idls/devnet/property.json`):**
- `claim_income(asset_id:u64)` disc `[6,24,199,191,118,141,95,123]`, 11 accts:
  [0] holder(signer) [1] payer(w,signer) [2] income [3] checkpoint(w)
  [4] holding [5] payment_mint [6] income_vault [7] vault_payment_account(w)
  [8] holder_payment(w) [9] payment_token_program [10] system_program.
  Seeds: income=["income",asset_id], checkpoint=["checkpoint",asset_id,holder],
  income_vault=["income-vault",asset_id]. Payment mint/vault accounts come from
  the income ledger (indexer `streams[i].mint`); for multiple streams the caller
  sends one `claim_income` per mint (builder takes the mint; wrapper composes).
- `close_income_checkpoint(asset_id:u64)` disc `[178,97,236,81,222,159,104,227]`,
  4 accts: [0] holder(signer) [1] rent_payer(w, not signer) [2] checkpoint(w)
  [3] holding. Single-signer-safe.
- `finalize_challenge(asset_id:u64)` disc `[184,38,132,51,103,143,203,9]`, 16 accts:
  [0] cranker(w,signer) [1] rent_payer(w) [2] config ["config"] [3] letting(w)
  ["letting",asset_id] [4] property [5] challenge(w)
  ["gov-challenge",asset_id,activeChallengeId] [6] agent_entry(w,optional)
  [7] xcav_mint [8] vault(w) ["vault"] [9] treasury [10] treasury_token(w)
  [11] challenger [12] challenger_token(w) [13] token_program
  [14] associated_token_program [15] system_program. `agent_entry` is Optional
  (None ⇒ property program id sentinel, like VoteOnAgent's previous_candidacy).
- `finalize_agent_election(asset_id:u64)` disc `[48,143,199,27,97,168,117,123]`,
  4 accts: [0] cranker(signer) [1] letting(w) [2] property [3] winner_entry
  (w, optional). Caller resolves winner from the indexer election state
  (`agentCandidacies` of the current round, max `votePower`; also pass their
  `DeriveAgentEntry`... NOTE: [3] is `agent_entry` ["agent", agent]).

- [ ] Failing tests first (discriminators, account order + PDA positions,
  arg encodings, Optional-None sentinels).
- [ ] Implement the 4 builders (+PDA) until green.
- [ ] `dotnet test PlutoFrameworkTests` green. Commit.

## Task 3: Call wrappers — all new flows + revote fix

**Files:**
- Modify: `realXmarketPlutoFramework/PlutoFrameworkCore/Xcavate/XcavateMarketplaceCallsModel.cs`
  (`SendPropertySharesAsync`, `CloseShareHoldingAsync`,
  `FinalizeSpvElectionAsync`, `ResolveSilentVerdictAsync`, `ExecuteDealAsync`;
  fix `VoteOnSpvLawyerAsync`'s `previousCandidate: null` at `:169` — resolve
  the caller's current-round vote from the indexer, pass its candidacy choice)
- Modify: `realXmarketPlutoFramework/PlutoFrameworkCore/Xcavate/XcavatePropertyCallsModel.cs`
  (`ClaimIncomeAsync(assetId, mint[])` — composes one `ClaimIncome` per stream
  mint found in the indexer income state, plus a trailing
  `CloseIncomeCheckpoint` when the checkpoint is fully drained (zero pending +
  zero new claimable) so the wallet never needs a separate cleanup action;
  `FinalizeProposalAsync`, `FinalizeChallengeAsync`,
  `FinalizeAgentElectionAsync`, `UnlockProposalVotesAsync`,
  `UnlockChallengeVotesAsync`, `UnlockAgentVotesAsync`; fix
  `VoteOnAgentAsync`'s `previousCandidate: null` at `:111` the same way)
- Modify: `realXmarketPlutoFramework/PlutoFrameworkTests/` wrapper-level
  account-resolution tests (mirror existing ones)

**Interfaces:**
- Wrappers follow the existing shape: `(SolanaCluster, walletAddress, ids...,
  CancellationToken)` → resolve state via config/indexer → return
  `Task<string>` signature via the caller's
  `XcavateMarketplaceTransactionModel.SubmitAsync` (wrappers themselves return
  the instruction list through the submit callback — check how
  `BuyRelistedSharesAsync` `:281` hands instructions to the popup flow and
  copy it exactly; SubmitAsync owns popups/errors).
- `SendPropertySharesAsync(cluster, sender, assetId, receiver, amount, ct)` —
  payer = `config.RentCollector` (2-signer). Blocks compile until it exists:
  `SendSharesPopupViewModel.cs:415` already calls it.
- `CloseShareHoldingAsync(cluster, cranker, assetId, owner, ct)` — single
  signer; rent refund goes to `config.RentCollector` matching the program's
  expectation... VERIFY against the deployed program: IDL acct [2] is
  rent_collector(w) read from config — builder already passes the config PDA,
  so wrapper passes `config.RentCollector` address.
- Crank wrappers are single-signer (cranker = connected wallet). They resolve
  listing/property state from the indexer themselves (winner candidacy,
  active proposal/challenge ids from `propertyLettings.governanceActive*`).
- [ ] Wrapper tests (happy-path resolution, recorded indexer responses
  pattern from `XcavateMarketplaceIndexerModelTests.cs`).
- [ ] `dotnet test PlutoFrameworkTests` green. Commit.

## Task 4: Indexer reads — income state, agent election, pending finalizations

**Files:**
- Modify: `realXmarketPlutoFramework/PlutoFrameworkCore/Xcavate/XcavateMarketplaceIndexerModel.cs`
  (+ records for `PropertyIncomeState`, `IncomeCheckpointState`,
  `AgentElectionState`, `PendingFinalizations`; claimable-amount math in
  `BigInteger` via `ParseBigInteger` `:819-826`)
- Modify: `realXmarketPlutoFramework/PlutoFrameworkTests/XcavateMarketplaceIndexerModelTests.cs`
  (recorded-JSON parsing tests incl. u128 larger than `ulong.MaxValue`,
  multi-stream mints, missing-checkpoint ⇒ all-cursors-zero, stale-round
  vote rows filtered out)

**Semantics (from the on-chain programs, verified in roadmap planning):**
- claimable(holder) per stream i = `(streams[i].per_share -
  checkpoint[i].per_share) * shares / 1e<X>` + `checkpoint[i].pending` —
  implement with BigInteger, share count from `HoldingState.Amount`; the
  exact divisor must match the program's scale factor — read it from
  `realxmarket-solana`'s property program if available locally, else derive +
  pin in tests from a recorded devnet example.
- `PendingFinalizations` for a listing/property: proposal ended (expiry <
  now, unfinalized via `governanceActiveProposal`), challenge ended,
  agent-election ended (`electionExpiry`), SPV election ended
  (`listing.spvElection.Expiry` + status), silent verdict (lawyer docStatus
  still PENDING past `legalDeadline`), execute_deal ready (status LEGAL /
  both lawyers APPROVED).
- `GetAgentElectionStateAsync(cluster, assetId, voter)` consuming the new
  `PropertyAgentElection.graphql`, client-side current-round filter.
- [ ] Failing parsing tests → implement → green. Commit.

## Task 5: Send shares UI

- Create: `realXmarketPlutoFramework/PlutoFramework/Components/XcavateProperty/SendSharesPopupView.xaml`
  (mirror `BuyRelistedSharesPopupView.xaml` structure §3.10, `FormInputView`
  for address + amount per `RelistPropertyTokensView.xaml:37-42`)
- Wire: DI registration + `PopupManager.TrackPopup` (`MauiAppBuilderExtensions.cs:157-291`),
  entry points on property detail (`PropertyDetailViewModel` — a "Send"
  action next to the owned/listed controls) and portfolio item
  (`InvestorMainPage.xaml:132-139` template or its detail navigation);
  popup copy must state that pending rent settles for both parties.
- [ ] Build green (`XcavateMobileApp.Android` head). Commit.

## Task 6: Rent / claim-income UI

- Portfolio page (`XcavateMobileApp/Pages/InvestorMainPage*`): "Rent
  available" card near the totals header — claimable total + "Claim all"
  (sequential per-property/per-mint txs through SubmitAsync's single status
  popup) + per-property claim affordance on each `PropertyThumbnailView`
  item (chip/button, only when claimable > 0).
- Property detail (`PropertyDetailViewModel` + page): income section —
  per-mint claimable amounts, "Claim rent" button, recent distribution
  history (from `propertyIncomes` events / streams deltas — if history isn't
  derivable client-side, fall back to Task 10's indexer query and show total
  paid instead).
- `close_income_checkpoint` is NEVER a separate button: appended silently to
  a claim tx when the checkpoint drains (wrapper logic from Task 3).
- [ ] Build green. Commit.

## Task 7: Agent election UI

- Governance section (`GovernanceVotingSectionView*` on `PropertyDetailPage.xaml:96-98`
  or new `AgentElectionSectionView`): sitting agent, election round/expiry,
  candidacy list with live vote power (`GetAgentElectionStateAsync`),
  Vote/Change-vote via `CastVotePopupViewModel` extended with an
  `"agent"` `VoteKind` + candidate context (previous-candidacy auto-resolution
  from Task 3 fixes the revote gap), "Unlock tokens" one-tap after
  participation (calls the unlock wrappers), "Finalize election" when the
  round expired (Task 8 cares for the banner — this section can host it).
- [ ] Build green. Commit.

## Task 8: Contextual crank banners + close empty position

- Property detail + marketplace listing pages: one-tap banners detected from
  `PendingFinalizations` (Task 4): "Voting ended — Finalize proposal",
  "Finalize challenge", "Finalize SPV election", "Resolve unresponsive
  lawyer", "Complete settlement (`execute_deal`)". Single-signer crank
  wrappers through SubmitAsync. Copy honest + friendly ("anyone can send
  this; it moves the process forward for everyone").
- Portfolio/property: "Reclaim SOL rent — close empty position" when a
  holding is zero and/or checkpoint drained: ONE tx bundling
  `close_share_holding` + `close_income_checkpoint` (both single-signer).
- [ ] Build green. Commit.

## Task 9: XcavateProfileApi — co-sign allowlist for property program + new discriminators (+ pushes)

(`~/XcavateProfileApi`, its own branch `feat/investor-income-cranks`)
- Extend `MarketplaceRentCollectorSigningService.cs`: today rejects any
  instruction not targeting the marketplace program (`:154-157`) and allows
  only 5 discriminators (`:14-18`). Add: property program id +
  `claim_income`/`close_income_checkpoint`, marketplace `send_property_shares`
  — and audit whether the existing property vote flows (vote_on_agent /
  proposal / challenge with rent-collector payer) also need allowlisting
  (likely the reason in-app votes were fragile); keep the deposit/refund-only
  semantics of the existing asset-spend validations untouched for the new
  discriminators except where the instruction semantics require the same
  checks.
- Push notifications when the indexer signals `income_distributed` (rent
  arrived) and "shares received" using the existing expo-push pipe (see how
  `OfferProtocol`-side pushes are sent; spec:
  `docs/superpowers/specs/2026-08-24-socketio-bucket-messages-design.md`).
- Server-side tests for the new allowlist cases.

## Task 10: Indexer + AssetDidCommDashboard

(`~/Indexer`, `~/AssetDidCommDashboard`, own branches)
- Indexer: computed `claimableIncome(owner, assetId)` (streams/checkpoint
  join server-side) and `pendingFinalizations` (derived from listing/letting
  rows the app already reads) GraphQL fields + `income_distributed`
  outbound webhook to the profile API. Tests per repo convention.
  NOTE: app-side Task 4 implements client-side math FIRST (works against
  today's indexer); the indexer query then replaces it transparently.
- AssetDidCommDashboard: `previous_candidacy` parity fix in the vote-on-agent
  flow (repo already has uncommitted `app/services/voting/*` edits —
  review whether that's this fix in flight; if so finish+commit it, else
  implement): an agent vote via the dashboard must pass the voter's current
  previous candidacy like the app's fixed wrapper.
- role-dashboard and xcavate-sumsub-webhook: explicitly out of scope.

## Task 11: E2E verification (devnet)

- `dotnet test PlutoFrameworkTests` + `PlutoFramework/PlutoFramework.Tests`
  equivalent green on both repos; `XcavateMobileApp.Android` build green.
- Devnet walkthrough (scripted where possible via a funded devnet wallet —
  else document the manual checklist): distribute income → claim →
  checkpoint closes when drained; vote agent → change vote → election ends →
  finalize (crank banner) → unlock; send shares → both checkpoints settle;
  close empty holding → SOL rent returned; proposal window expiry → finalize
  proposal banner; SPV election / silent-verdict / execute-deal banners on
  seeded states.
- Parent-repo submodule pointer bump on `feat/investor-income-cranks`,
  then finishing-a-development-branch.
