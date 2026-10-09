# Property governance voting on the detail page — 2026-10-06 (revised 2026-10-07)

The property detail page surfaces every vote an investor can cast on-chain, per Figma
"2.6.0 Details Page" — using ONLY the instructions the deployed programs already have:
the SPV-lawyer election during the legal phase (the "terms" flow: Approve terms backs
the lawyer's candidacy) and the letting seat's spending proposals and challenges once
the property is finalized. An earlier design added a dedicated on-chain
terms-ratification vote; it was deliberately NOT shipped — `vote_on_spv_lawyer` already
IS the pre-settlement investor vote, and not voting is the on-chain "reject".

## On-chain surface (realxmarket-solana, unchanged programs)

- Marketplace `vote_on_spv_lawyer(listing_id, amount)`: share-weighted vote backing one
  candidacy (`lawyer-candidate` / `lawyer-vote` PDAs, round-seeded); the listing's own
  `spvElection*` counters track the live round. Election results read from the listing
  (an assigned `spvLawyer` = ratified).
- Property `vote_on_proposal` / `vote_on_challenge` (+ `vote_on_agent`).

## Data flow

Indexer GraphQL (`XcavateMarketplaceIndexerModel.GetGovernanceStateAsync`) answers one
`XcavateGovernanceState`: the SPV election assembled from the listing fragment's
`spvElection*` fields plus `MarketplaceLawyerCandidacies` (summed vote power + leading
candidacy), the letting seat counters (`PropertyLettingGovernance`), the live
proposal/challenge (`PropertyProposals`/`PropertyChallenges`) and the caller's share
ledger (`MarketplaceShareHoldingOf`). The page refreshes it inside
`PropertyDetailViewModel.RefreshListingAsync`, which the transaction-confirmed hook
already re-fires — so casting a vote updates the page's own tallies. The split bar is
turnout over the sold supply (`totalVotePower / soldShareAmount`).

Voting power = `amount − listed − max(locks)`: on-chain the effective lock is the
largest single reason's counter, mirrored in `HoldingState.Votable`.

## Casting

- `TermsVotingSectionView` renders the Figma banner + countdown + turnout bar with an
  inline "Approve terms" button; `GovernanceVotingSectionView` renders the
  proposal/challenge cards with Yes/No buttons.
- `CastVotePopupView` (one shared sheet, `VoteKind` terms/proposal/challenge) carries
  the Figma confirmation sentence; Submit votes through
  `XcavateMarketplaceTransactionModel.SubmitAsync` (investor + the config's rent
  collector co-signing via the profile API, like purchases).
- Instruction encoders: `XcavateMarketplaceProgram.VoteOnSpvLawyer` and
  `XcavatePropertyProgram` (+ calls models), discriminators pinned by
  `PlutoFrameworkTests`.

## Messenger touchpoint

The realXmessenger SPA announces the same votes as `voting/*` bucket messages (posted
by the profile API's `system:voting` identity from indexer webhooks:
`spv_case_claimed` / `spv_lawyer_elected` for the "terms" flow). Casting from the
messenger works inside the app's WebView (the SolanaWalletStandardBridge's
`signAndSendTransaction`); elections there are approve-only too.
