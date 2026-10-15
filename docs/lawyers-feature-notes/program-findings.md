# Lawyers feature — Solana program findings (verified)

Source of truth for the on-chain side. All facts below were verified by full reads of
`/home/rosta/realxmarket-solana/programs/marketplace/src/` (legal.rs, lawyers.rs, election.rs,
state.rs) in this session. Discriminators verified by script — see
`docs/lawyers-feature-notes/verification-checklist.md` lines 95–106.

## Constants (state.rs)

- `MAX_SPV_CANDIDATES = 5`
- `PRICE_DECIMALS = 9` — all prices/costs quoted at tGBP 9-decimals
- `ListingStatus`: PendingAssets, Listed, SoldOut, Legal, Finalized, Expired, Cancelled, Refunding
- `DocumentStatus`: Pending (default), Approved, Rejected
- `LockReason::LawyerElection` is one of 4 lock slots on `ShareHolding`

## PDA seeds (program side)

- `Lawyer` account: `[LAWYER_SEED, lawyer_key]`
- `LawyerCandidacy`: `[LAWYER_CANDIDATE_SEED, listing_id_le, round_le, lawyer]`
- `LawyerVote`: `[LAWYER_VOTE_SEED, listing_id_le, round_le, voter]`
- `ShareHolding`: `[SHARE_SEED, listing_id_le, owner]`
- `Config`, `Vault`, `Listing`, `Property` per existing app encoders
- The mobile app already has PDAs: DeriveLawyer, DeriveLawyerCandidacy, DeriveLawyerVote,
  DeriveRoleAccount, DeriveRegion, DeriveVault, DeriveConfig, DeriveListing, DeriveProperty

## State structs

### Lawyer (registry account)
`{ lawyer Pubkey, region_id u16, deposit u64, active_cases u32, bump }`

### LawyerAssignment (embedded in Listing, one per side)
`{ lawyer Pubkey (default until engaged), costs u64 (only SPV side is paid from fees;
developer side stays 0), doc_status DocumentStatus, documents_hash [u8;32] (zero while pending) }`

### Listing lawyer fields
- `developer_lawyer: LawyerAssignment`
- `spv_lawyer: LawyerAssignment`
- `second_attempt: bool`
- `developer_engaged: bool` (sticky; timeout without it costs developer 1% of bond)
- `spv_costs_due: u64`, `spv_costs_payee: Pubkey` (stamped at cancellation; kept because
  close_case clears the assignment before fees settle)
- `collected_fee_quote: u64` (investor fees collected; caps SPV lawyer costs)
- `spv_election: SpvElection { expiry i64 (0 = no round running), candidate_count u32, round u64 }`
- `legal_deadline: i64` (0 until last share sells)
- `legal_process_time`, `lawyer_voting_time`, `min_voting_quorum_bps` — all snapshotted at listing time

### LawyerCandidacy
`{ listing_id, round, lawyer, costs, vote_power u32, rent_payer, bump }`

### LawyerVote
`{ listing_id, round, voter, choice, power u32, rent_payer, bump }`

### ShareHolding
`votable() = amount − listed; locked() = max of 4 lock counters`

### Config
`{ xcav_mint, rent_collector, lawyer_deposit u64, claiming_time, legal_process_time,
lawyer_voting_time, min_voting_quorum_bps, ... }`

## check_engagement_open (legal.rs:14–32)

Shared by `assign_developer_lawyer` + `claim_spv_case`. Requires:
1. `listing.status == SoldOut`
2. `now <= listing.legal_deadline`
3. `registry.region_id == property.region_id` (else `WrongRegion`)

## The instructions (exact accounts in order, args, rules, events)

Instruction order matters. Encoders live in the mobile app
(`XcavateMarketplaceProgram.cs`), call builders in `XcavateMarketplaceCallsModel.cs`.
**Constraint: `XcavateMarketplaceTransactionModel.SubmitAsync` has a hard 2-signer cap
(wallet + rent collector via RentCollectorSignatureClient).** All account metas in the app
encoders are writable (deployed-binary requirement; ignore IDL readonly flags).

### 1. register_lawyer (lawyers.rs) — 2-signer
Accounts in order:
1. `lawyer` (Signer)
2. `payer` (Signer, `address = config.rent_collector`) — the wallet + rent collector pair
3. `config` (PDA)
4. `lawyer_role` (whitelist RoleAccount, Lawyer seed byte, must be compliant)
5. `region` (regions-program PDA by region_id)
6. `lawyer_account` (init, payer = payer, seeds `[LAWYER_SEED, lawyer.key()]`)
7. `xcav_mint`
8. `lawyer_token` (mint + authority = lawyer)
9. `vault` (`[VAULT_SEED]`, authority = config)
10. `token_program`
11. `system_program`

Args: `region_id u16`, `max_deposit u64`.
Deposit = `config.lawyer_deposit` (≤ max_deposit). Transfer checked
`lawyer_token → vault` (`lock_to_vault`). Stores lawyer/region_id/deposit/active_cases=0.
Emits `LawyerRegistered`.

### 2. unregister_lawyer (lawyers.rs) — 2-signer
Accounts: `lawyer` (Signer), `config`, `rent_collector` (UncheckedAccount,
`address = config.rent_collector`), `lawyer_account` (mut, close = rent_collector,
requires `active_cases == 0` else `LawyerStillActive`), `xcav_mint`, `lawyer_token`,
`vault`, `token_program`.
Refund via `release_from_vault`. Emits `LawyerUnregistered`.
**Deliberately NOT role-gated (exit path).**

### 3. claim_spv_case (legal.rs:130–272) — single wallet (lawyer)
Accounts: `lawyer` (Signer), `payer` (Signer, mut, **ANY wallet** — lawyer can be payer
⇒ single-wallet path, lawyer signs both), `lawyer_role` (Lawyer, compliant),
`registry` (Lawyer PDA), `listing` (mut), `property` (read; requires `spv_created` else
`SpvNotCreated`), `candidacy` (init, payer = payer, seeds
`[LAWYER_CANDIDATE_SEED, listing_id_le, round_le, lawyer.key()]`), `system_program`.

Args: `listing_id u64`, `round u64`, `costs u64`.

Rules: `check_engagement_open`; `property.spv_created`; `spv_lawyer` unassigned else
`LawyerJobTaken`; `lawyer != developer_lawyer` else `ConflictOfInterest`;
`costs <= listing.collected_fee_quote` else `CostsExceedFees` (SPV lawyer paid from the fee
pot; the developer lawyer is private).

Round logic: `expiry == 0` → first candidacy opens the round: require
`round == election.round + 1` (else `WrongElectionRound`),
`expiry = (now + lawyer_voting_time).min(legal_deadline)`, `candidate_count = 0`.
Else require `round == election.round && now < expiry` (else `VotingClosed`).
`candidate_count + 1 <= 5` (else `TooManyCandidates`).
Fills candidacy `{ costs, rent_payer = payer, bump }`.
Emits `SpvCaseClaimed { listing_id, round, lawyer, costs, expiry }`.

**Key finding: claim_spv_case does NOT need the rent collector — earlier
"wallet + rent collector" lead was WRONG for this instruction.**

### 4. resign_from_case (legal.rs:278–325) — lawyer wallet only
Accounts: `lawyer` (Signer), `registry` (mut, `LAWYER_SEED + lawyer`), `listing` (mut).
Args: `listing_id u64`.
**Lawyer-wallet only, NOT role-gated (exit path).**
Finds the side (developer or spv) whose lawyer == caller else `NotCaseLawyer`;
requires `side.doc_status == Pending` else `AlreadyConfirmed`;
clears `side.lawyer = 0`, `side.costs = 0`; `registry.active_cases -= 1`.
Emits `LawyerResigned { listing_id, lawyer }`.

### 5. confirm_documents (legal.rs:334–457) — lawyer wallet
Accounts: `lawyer` (Signer), `lawyer_role` (Lawyer role PDA; role presence required but
NOT compliance-checked here — "compliance was vetted when they engaged"), `listing` (mut).
Args: `listing_id u64`, `approve bool`, `documents_hash [u8;32]`.

Rules: status SoldOut; `now <= legal_deadline` else `LegalProcessExpired`;
`documents_hash != zeros` else `EmptyDocumentsHash`.
Side determined by which lawyer matches the caller else `NotCaseLawyer`.
Verdicts only combine on the same hash: require
`other.doc_status == Pending || other.documents_hash == documents_hash` else
`DocumentsMismatch`; own side must be Pending else `AlreadyConfirmed`.
Sets `side.doc_status = approve ? Approved : Rejected`, `side.documents_hash = hash`.

Combine logic:
- (Approved, Approved) → `listing.status = Legal` (sale settles)
- (Rejected, Rejected) → `cancel_sale`
- split → if `second_attempt` → `cancel_sale`, else both sides reset to Pending + zero
  hashes + `second_attempt = true` (one revision round)

Emits `DocumentsConfirmed { listing_id, lawyer, approve, documents_hash, status }`.
This is the instruction lawyers call after reviewing the documents in the bucket; the hash
is the 32-byte identifier of the document set they reviewed (IPFS-derived — off-chain,
see services-findings.md).

### 6. resolve_silent_verdict (legal.rs:464–510) — permissionless crank
Accounts: `cranker` (Signer), `listing` (mut).
Args: `listing_id u64`.
Rules: status SoldOut; `now <= legal_deadline`;
`now >= legal_deadline − legal_process_time/5` (final stretch, else `VerdictWindowOpen`);
`spv_ruled && !dev_ruled` else `NoVerdictPassed` (one-directional: SPV side already ruled,
developer side silent).
Sets developer side to Rejected + `cancel_sale` (stamps spv_costs_due/payee).
Emits `SilentVerdictResolved { listing_id, silent_lawyer }`.
The developer-side lawyer's pay never depends on the SPV side choosing to rule; the reverse
case just rides to the deadline timeout exit.

### 7. close_case (legal.rs:515–564) — permissionless crank, once per side
Accounts: `cranker` (Signer), `listing` (mut), `registry` (mut, `LAWYER_SEED + lawyer_arg`).
Args: `listing_id u64`, `lawyer Pubkey`.
Requires status ∈ {Cancelled, Refunding, Expired} else `CaseStillOpen`.
Clears that side (lawyer = 0, costs = 0); `registry.active_cases -= 1` (so the lawyer can
unregister). Second call on the same side fails with `NotCaseLawyer` (assignment cleared).
Emits `CaseClosed { listing_id, lawyer }`.

### 8. assign_developer_lawyer (legal.rs:40–124) — developer wallet
Accounts: `developer` (Signer), `developer_role` (RealEstateDeveloper RoleAccount PDA,
seeds::program whitelist), `lawyer_role` (Lawyer RoleAccount PDA for the NAMED lawyer,
must be compliant — the named lawyer need not sign), `registry` (Lawyer PDA for named
lawyer, mut), `listing` (mut, constraint `listing.developer == developer` else
`NotListingDeveloper`), `property` (PropertyAsset PDA).
**6 accounts, no system_program, no payer.**
Args: `listing_id u64`, `lawyer Pubkey`.
Rules: `check_engagement_open`; `developer_lawyer` unassigned else `LawyerJobTaken`;
`lawyer != spv_lawyer` else `ConflictOfInterest`; `registry.active_cases += 1`; sets
`developer_lawyer { lawyer, doc_status = Pending }`, `developer_engaged = true`.
Emits `DeveloperLawyerAssigned { listing_id, lawyer }`.
Developer-side only — likely out of mobile-app lawyer scope, but list it.

### 9. vote_on_spv_lawyer (election.rs) — investor wallet
Accounts: `voter` (Signer), `payer` (Signer, ANY wallet — "one protocol key can never
decide the election by withholding its signature"; record remembers rent_payer),
`voter_role` (RealEstateInvestor only), `listing` (mut),
`holding` (SHARE_SEED + listing_id + voter), `vote_record` (init_if_needed,
LAWYER_VOTE_SEED + listing_id + round + voter), `candidacy` (mut, current round),
`previous_candidacy` (Option, mut — the candidate they previously voted for, for revote),
`system_program`.
Args: `listing_id u64`, `amount u32` = **share count (NOT XCAV)**.
Rules: `election.expiry != 0`; status SoldOut; `now < expiry`; `amount > 0`; candidacy
round must match; revote subtracts old power from old candidacy;
`locked_after <= holding.votable()` else `NotEnoughShares`;
`holding.locks[LawyerElection] += amount`; `candidacy.vote_power += amount`.

### 10. finalize_spv_election (election.rs) — permissionless crank
Accounts: `cranker` (Signer), `listing` (mut), `property` (PropertyAsset PDA),
`winner_registry` (Option<UncheckedAccount>), **ALL candidacies of the round as
remaining_accounts** (count == candidate_count, each verified for listing_id/round, deduped).
Args: `listing_id u64`.
Rules: `expiry != 0`; `now >= expiry`;
quorum: `total_votes * 10_000 > property.share_amount * listing.min_voting_quorum_bps`.
Assign only if `quorum && SoldOut && !tied && top_power > 0 && now <= legal_deadline &&
winner != developer_lawyer && winner_costs <= collected_fee_quote && winner's Lawyer PDA
exists with region_id == property.region_id`.
On assign: winner registry `active_cases += 1`;
`listing.spv_lawyer = { winner, winner_costs, doc_status = Pending }`.
Always: `election.expiry = 0`, `candidate_count = 0` (round number kept — next round =
round + 1).

**Quirk to check empirically in tests/legal.rs (994 lines): the else-branch
`err!(WrongLawyer)` when winner_registry is None sits INSIDE the
`if quorum_met && …` block — so even a losing round might need the winner's registry
supplied. Verify by reading tests/legal.rs before finalizing the guide's account-list
instructions.**

### close_candidacy (election.rs) — permissionless crank
Accounts: `cranker` (Signer), `rent_payer` (UncheckedAccount,
`address = candidacy.rent_payer`), `listing` (UncheckedAccount, may already be torn down),
`candidacy` (mut, close = rent_payer, seeds LAWYER_CANDIDATE_SEED + listing_id + round + lawyer).
Allowed once that round is settled/superseded (`round != election.round || expiry == 0`)
else `VotingStillOngoing`. Refunds the fronted rent.

### unlock_voting_shares (election.rs)
Accounts: `voter` (Signer), `rent_payer` (the vote_record's rent_payer), `listing`,
`holding`, `vote_record` (close = rent_payer, seeds … + round + voter).
Not allowed while that round is open (`round == election.round && expiry != 0 && SoldOut
&& now < expiry`).
**OPEN: whether the investor can be the rent_payer (single signature) or whether a
co-signing rent collector is needed when the collector fronted it — check election.rs
handler or existing app builder code before stating in the guide.**

## Events (for indexer mapping)

- `DeveloperLawyerAssigned { listing_id, lawyer }`
- `SpvCaseClaimed { listing_id, round, lawyer, costs, expiry }`
- `LawyerResigned { listing_id, lawyer }`
- `DocumentsConfirmed { listing_id, lawyer, approve, documents_hash, status }`
- `CaseClosed { listing_id, lawyer }`
- `SilentVerdictResolved { listing_id, silent_lawyer }`
- `LawyerRegistered` / `LawyerUnregistered` (lawyers.rs)
- Election events from election.rs (names to confirm when reading indexer mappings;
  the two app query files MarketplaceLawyerCandidacies/MarketplaceLawyerVotes already
  exist so those are indexed)

## IDL discriminators (verified by script)

Also recorded in `docs/lawyers-feature-notes/verification-checklist.md` lines 95–106.

| instruction | discriminator | args |
|---|---|---|
| register_lawyer | [165,135,154,240,244,159,91,255] | region_id u16, max_deposit u64 |
| unregister_lawyer | [184,26,138,218,96,199,142,99] | — |
| claim_spv_case | [175,185,102,63,166,39,168,56] | listing_id u64, round u64, costs u64 |
| resign_from_case | [158,242,127,206,24,126,203,66] | listing_id u64 |
| close_case | [188,7,71,62,186,245,29,55] | listing_id u64, lawyer pubkey |
| lawyer_confirm_documents | [218,145,193,157,127,110,10,94] | listing_id u64, approve bool, documents_hash [u8;32] |
| assign_developer_lawyer | [197,167,242,89,235,9,169,65] | listing_id u64, lawyer pubkey |
| close_candidacy | [219,28,41,13,28,103,134,84] | listing_id u64, round u64, lawyer pubkey |
| finalize_spv_election | [2,249,39,73,224,169,145,100] | listing_id u64 |
| vote_on_spv_lawyer | [245,181,143,227,83,159,141,147] | listing_id u64, amount u32 |

## Signer matrix (settled)

- **2-signer (wallet + rent collector via ProfileApi co-signing whitelist):
  register_lawyer, unregister_lawyer only.**
- **Single wallet (lawyer's wallet): claim_spv_case (lawyer == payer allowed),
  resign_from_case, confirm_documents, assign_developer_lawyer (developer's wallet).**
- **Single wallet (investor): vote_on_spv_lawyer, unlock_voting_shares
  (investor + their vote record's rent_payer — check whether the investor can be the
  rent_payer; if the rent collector fronted it, that's a second signature — verify the
  pattern in existing app code/tests before stating).**
- **Permissionless (any wallet): finalize_spv_election, close_candidacy,
  resolve_silent_verdict, close_case.**
- **OPEN: whether ProfileApi's `MarketplaceRentCollectorSigningService` whitelist already
  includes register_lawyer/unregister_lawyer, and where to add them if not — the #1
  remaining unknown.**

## Settle / cancel flow logic

- Both approve (same hash) → status `Legal` → settlement pays developer less fees; SPV
  lawyer paid from collected fees, capped by `collected_fee_quote`.
- Both reject / second split / deadline timeout → `cancel_sale` stamps
  `spv_costs_due`/`spv_costs_payee`, status `Cancelled` → refunds open → `close_case` per
  side releases lawyers.
- `resolve_silent_verdict` covers the SPV-ruled-dev-silent case in the final 1/5 of the
  window.

## Settled decisions (do not reopen)

- Onboarding identical to investor (`UserRoleEnum.Lawyer` → Sumsub level
  "csharp-verification-lawyer", `PlutoFramework/Model/Xcavate/XcavateUserModel.cs:25`)
- Legal bucket is 100% off-chain (grep "bucket" across all of realxmarket-solana → zero
  matches)
- Doc upload is the missing mobile piece (IPFS acceptable)
- Plan date 2026-10-14; income UI out of scope; expired-claim out of scope
