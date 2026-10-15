# Lawyers feature — verification checklist (run after transcription completes)

Subagents are quota-blocked (403, 5h window). Do these with direct Grep/Read.
Results go into the two findings files below, then fold into
`docs/superpowers/plans/2026-10-14-lawyers-feature.md`.

## A. Program + Indexer → docs/lawyers-feature-notes/program-findings.md
Sources: /home/rosta/realxmarket-solana (Rust program; marketplace may be in a subdir),
/home/rosta/Indexer, cross-check idls/devnet/marketplace.json.

For each instruction — account order (signer / payer / writable / who-is-who), args with
types, state written, key validation rules, file:line citations:
register_lawyer, unregister_lawyer, claim_spv_case, resign_from_case, close_case,
lawyer_confirm_documents, assign_developer_lawyer, close_candidacy,
finalize_spv_election, vote_on_spv_lawyer.

Business-logic answers:
- register_lawyer: deposit handling (escrowed to vault?), what lawyer_account PDA stores
  (region, active?), LawyerDeposit config interplay with max_deposit arg.
- unregister_lawyer: preconditions (active cases?), deposit refund path.
- claim_spv_case: `costs` validation (min/max? paid by lawyer or rent collector?),
  round semantics / secondAttempt, what the candidacy PDA stores.
- vote_on_spv_lawyer: vote power source (XCAV holdings?), meaning of `amount u32`,
  previous_candidacy optional account (challenger mechanic?).
- finalize_spv_election: winner rule, listing fields set (spvLawyer, spvCostsDue/Payee,
  legalDeadline, collected, secondAttempt), legal-phase trigger (buckets? deadlines?).
- lawyer_confirm_documents: caller (SPV lawyer vs developer lawyer), `approve` bool
  semantics, where documents_hash lands (spvLawyerDocumentsHash vs developerLawyerDocumentsHash).
- assign_developer_lawyer: caller (developer?), preconditions.
- resign_from_case / close_case / close_candidacy: effects, permissionless-crank semantics.
- grep repo for "bucket" (case-insensitive): on-chain or off-chain?

Indexer (/home/rosta/Indexer):
- lawyer REGISTRY table: exists? name/columns. lawyer_candidacies + lawyer_votes tables.
- GraphQL: `lawyers` query present? If table exists but query missing, where would it be
  added (cite sibling query files as pattern).
- Known app queries: marketplaceLawyerCandidacies (listingId, round, lawyer, costs,
  votePower), marketplaceLawyerVotes (listingId, round, voter, choice, power),
  userRoles, userHasRole.

## B. Off-chain services → docs/lawyers-feature-notes/services-findings.md
Sources: /home/rosta/XcavateProfileApi, /home/rosta/xcavate-sumsub-webhook,
/home/rosta/role-dashboard, /home/rosta/AssetDidComm. file:line citations for all.

1. ProfileApi Sumsub: level → role mapping. Is `csharp-verification-lawyer` present?
   On GREEN, what exactly grants the Lawyer role (table/column/API)? Admin permission
   like `permission.lawyer`?
2. ProfileApi rent-collector co-signing: find MarketplaceRentCollectorSignatureService
   (or similar). Which marketplace instructions whitelisted (name or discriminator)?
   Is claim_spv_case (bytes 175,185,102,63,166,39,168,56) in it? Where to add a new one?
   Client endpoint for requesting a co-signature (route, auth, payload).
3. Legal bucket: search all four repos for "bucket"/legalBucket. Service that creates
   the legal bucket when legal phase starts? Read/write grants (winning lawyer?
   developer? all lawyers)? Upload API the app would use?
4. xcavate-sumsub-webhook: purpose, events/levels handled, lawyer relevance or legacy?
5. role-dashboard: who uses it, data sources (indexer GraphQL? profile API?), any
   lawyer-facing views (cases, candidacies, voting, documents), what's missing.
6. AssetDidComm: role in product; used in lawyer/legal flow?

## Already verified in mobile app (safe to cite, from earlier direct reads)
- XcavateMarketplaceProgram.cs (realXmarketPlutoFramework/PlutoFrameworkCore/Xcavate/):
  hand-built encoders, discriminators sha256("global:<name>")[0..8], ALL account metas
  writable (deployed binary requirement — ignore IDL readonly flags). Existing PDAs:
  DeriveLawyer, DeriveLawyerCandidacy, DeriveLawyerVote, DeriveRoleAccount, DeriveRegion,
  DeriveVault, DeriveConfig, DeriveListing, DeriveProperty. Existing encoders include
  VoteOnSpvLawyer, FinalizeSpvElection, CreateSpv.
- XcavateMarketplaceCallsModel.cs: Task<List<TransactionInstruction>> *Async(cluster,
  address, ...) resolving state from indexer + RPC (ReserveSharesAsync,
  BuyPropertySharesAsync, CreateSpvAsync, Withdraw*Async). New lawyer call builders go here.
- XcavateMarketplaceTransactionModel.cs (PlutoFramework/Components/XcavateProperty/):
  SubmitAsync(description, Func<string, SolanaCluster, CancellationToken,
  Task<List<TransactionInstruction>>>); hard 2-signer cap (wallet + rent collector via
  RentCollectorSignatureClient); blockhash-expiry retry x2; TransactionConfirmed /
  SubmissionFailed events; SolanaSubmissionErrorDescriber.
- XcavateProgramAddresses.cs: Devnet XcavateProgramSet (Marketplace
  dj9Q3CpHvDHwexCbkgJ5APDx4JsTxPssNebkvP15g1T, Whitelist 7TrzjKpdrEhnfhxuw8tWdH1sjxadazscsG5HXCDPLmaY,
  Property deCp9srk9C6P4BXJaFpjR5H6Jsm6DCq8AL2kk338dVq, Regions
  5iupkzVtWxee48UXh3s615V9sXXuYjsSr61VPuduXdPc); Mainnet=null; gated on
  XcavateDeploymentModel.IsDeployed + Require(cluster).
- WhitelistModel.cs: XcavateRole.Lawyer=3; HasRoleAsync gates on `compliant`
  (UserHasRole query). XcavateMarketplaceConfigModel: Parse(byte[]) on-chain config
  (RentCollector, XcavMint, LawyerDeposit, LawyerVotingTime).
- Indexer GraphQL files (Indexers/XcavateDevnetIndexer/Queries/):
  MarketplaceLawyerCandidacies.graphql, MarketplaceLawyerVotes.graphql, UserRoles.graphql,
  UserHasRole.graphql, MarketplaceListings.graphql (carries developerLawyer, spvLawyer,
  spvLawyerCosts, spvLawyerDocStatus, spvLawyerDocumentsHash, developerLawyerDocStatus /
  DocumentsHash, spvElectionExpiry/Round/CandidateCount, spvCostsDue/Payee, collected,
  secondAttempt, legalDeadline, lawyerVotingTime), MarketplaceConfigInfo.graphql.
  NO lawyers-registry query yet → likely new MarketplaceLawyers.graphql.
- UI: PropertyDetailViewModel.cs SPV election section (SpvElectionSectionIsVisible,
  SpvCanVote, ApproveSpvTerms → VoteOnSpvLawyer), TermsVotingSectionView.xaml.
  NO lawyer-side UI (register/claim/documents/resign). Onboarding: UserRoleEnum.Lawyer
  → Sumsub level "csharp-verification-lawyer" (PlutoFramework/Model/Xcavate/
  XcavateUserModel.cs:25); UserTypeSelectionPage, XcavateRoleBadgeView, UserTypeBadgeView.
- IDL lawyer instruction discriminators (verified by script):
  register_lawyer [165,135,154,240,244,159,91,255] (region_id u16, max_deposit u64);
  unregister_lawyer [184,26,138,218,96,199,142,99] (no args);
  claim_spv_case [175,185,102,63,166,39,168,56] (listing_id u64, round u64, costs u64);
  resign_from_case [158,242,127,206,24,126,203,66] (listing_id u64);
  close_case [188,7,71,62,186,245,29,55] (listing_id u64, lawyer pubkey);
  lawyer_confirm_documents [218,145,193,157,127,110,10,94] (listing_id u64, approve bool,
  documents_hash [u8;32]);
  assign_developer_lawyer [197,167,242,89,235,9,169,65] (listing_id u64, lawyer pubkey);
  close_candidacy [219,28,41,13,28,103,134,84] (listing_id u64, round u64, lawyer pubkey);
  finalize_spv_election [2,249,39,73,224,169,145,100] (listing_id u64);
  vote_on_spv_lawyer [245,181,143,227,83,159,141,147] (listing_id u64, amount u32).

## Settled decisions (from the boss call; do not reopen)
- Onboarding identical to investor (boss confirmed).
- Legal bucket already exists / already ready (boss confirmed).
- Document upload is THE missing piece; IPFS is acceptable.
- Deliverable = docs/superpowers/plans/2026-10-14-lawyers-feature.md (guide, no code).
- Boss wants a reliable automated multi-actor E2E test setup (agents with IDs running
  the flow from multiple devices/actors); human tester team joins later.
- "Can the lawyers review documents and then vote? — Yeah."
