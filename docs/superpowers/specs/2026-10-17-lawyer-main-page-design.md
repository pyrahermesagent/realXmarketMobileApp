# Lawyer main page — design

Date: 2026-10-17
Status: approved for planning (auto-mode session; user directive "let's get working on the Lawyer Side")

## Intent

Give lawyer users their own landing experience in the Xcavate mobile app, mirroring
`InvestorMainPage`. Three capabilities:

1. **Stat cards** — "Manage properties" (count of properties the lawyer has claimed / is
   assigned to) and the standard Solana **Balance** cell.
2. **Terms document card** — the lawyer uploads (or replaces) a single terms PDF. The app
   posts it to XcavateProfileApi, which pins it to IPFS and stores the resulting CID keyed
   by the lawyer's wallet address. A "View" action opens an in-app page rendering the PDF.
3. **Property list** — same list UX as the investor page, but the filters are
   **"Newly listed"** (listings where a lawyer can still be engaged: on-chain that means
   `status == SoldOut`, legal deadline not passed, at least one lawyer slot vacant —
   sorted latest-to-oldest by when the listing sold out) and **"Under Instruction"**
   (listings where the connected wallet is the developer-side or SPV-side lawyer).

Supporting changes land in two sibling repos:

- `~/Indexer` (Rust + Carbon, juniper GraphQL in `crates/api`): extend the `listings`
  query with lawyer filters. No DB migration required.
- `~/XcavateProfileApi` (ASP.NET Core net10, EF Core Postgres): new terms-document
  endpoints + first IPFS (Pinata) integration + new EF entity/migration.

## Out of scope

- Claim/resign/confirm-documents on-chain transactions from this page (existing
  encoders; separate task).
- Region matching, conflict-of-interest exclusion enforced query-side beyond the simple
  lawyer-exclusion noted below (on-chain validation remains the hard gate).
- Deleting a terms document.
- Income UI, expired-claim handling (per settled decisions in
  `docs/lawyers-feature-notes/program-findings.md`).
- Publishing a new `XcavateProfileApiClient` NuGet; the app hand-rolls the new HTTP calls
  (precedent: `RentCollectorSignatureClient`).

## Component 1 — Indexer (`/home/rosta/Indexer`)

Extend the existing `listings` query (`crates/api/src/graphql/query.rs:594-617`,
resolver `crates/api/src/graphql/programs/marketplace.rs:1153-1355`) with two optional
args (juniper camelCases them):

- `assignedLawyer: String` (base58) — `WHERE developer_lawyer = $n OR spv_lawyer = $n`.
  Powers "Under Instruction". Ordering stays the house default `slot DESC, pubkey ASC`.
- `assignableTo: String` (base58, optional value) — `WHERE status = 'SOLD_OUT'
  AND (legal_deadline = 0 OR legal_deadline > now-epoch)
  AND (developer_lawyer = VACANT OR spv_lawyer = VACANT)`
  (`VACANT` = 32 zero bytes, sentinel already in
  `crates/api/src/graphql/programs/derived.rs:24-26`), and when the arg carries an
  address, additionally exclude listings where that address already sits in either slot.
  Ordering switches to the sold-out transition: `LEFT JOIN sold_out_notifications sn
  ON sn.listing_id = l.listing_id ORDER BY sn.slot DESC NULLS LAST, l.slot DESC,
  l.pubkey ASC` (the notification table is the only first-SOLD_OUT record; one row per
  listing, PK-guaranteed). Powers "Newly listed".

Both args compose with existing filters (`first`/`offset` pagination unchanged); the
`count(*)` arm gets the same predicates. After the change: refresh `.sqlx`
(`cargo sqlx prepare` in `crates/api`) and add tests in
`crates/api/src/graphql/tests.rs` following
`listings_filter_by_feed_state_partitions_every_listing` (seed via `seed_listing_row`).

Repo rules (Indexer AGENTS.md) apply: work on branch `agent/20261017-lawyer-listing-filters`,
never `main`; additive only; `cargo fmt` (not `--all`); clippy clean; lint-migrations
untouched (no migration). Ship via PR for human merge.

## Component 2 — XcavateProfileApi (`/home/rosta/XcavateProfileApi`)

New capability: terms-document upload to IPFS with CID persistence per wallet.

**IPFS service**: `Services/Ipfs/PinataIpfsService.cs` implementing
`IIpfsService { Task<string> PinFileAsync(Stream, fileName, contentType, CancellationToken)
→ CID }`. Plain `HttpClient` multipart POST to `https://api.pinata.cloud/pinning/
pinFileToIPFS` with `Authorization: Bearer <PINATA_JWT>` — no new NuGet dependency.
Gateway base from `IPFS_GATEWAY_URL` (default `https://gateway.pinata.cloud/ipfs/`).
Registered in `Program.cs` next to the S3 block (:122-133); **absent/empty `PINATA_JWT`
= feature off**, endpoints return 503 (house convention). Env vars documented in
`.env.example` + `docker-compose.yml`.

**Entity**: new `LawyerTermsDocument` in `ProfileDbContext` (mirrors `WalletMigration`,
avoids touching `Profile`'s signed-hash surface): `Ss58Address` (PK string), `Cid`,
`FileName`, `ContentType`, `UpdatedAt`. EF migration via `dotnet ef migrations add`;
auto-applied on startup.

**Endpoints** — new `TermsDocumentController`, `[Route("api/profiles/{ss58address}/terms-document")]`:

- `POST /` — multipart `IFormFile`, signed request (manual header validation via
  `ISignatureValidator` exactly like `ProfilesController.UploadImageAsync`, signing
  `EmptyPayloadBody`), owner-or-admin 403 check, `.pdf`/`application/pdf` allow-list
  (extend `ImageUploads` conventions), request size limit. Pins to IPFS, upserts the row,
  returns `Ok(TermsDocumentResponse { cid, url, fileName, updatedAt })`.
- `GET /` — public (terms are meant to be readable by counterparties). 404 when none.

Keep the `Async` suffix on action names (`SuppressAsyncSuffixInActionNames = false`).

**Client SDK**: add `TermsDocumentResponse` model + `GetTermsDocumentAsync` /
`UploadTermsDocumentAsync` to `XcavateProfileClient` following `UploadImageAsync`
(:174-207). The mobile app will NOT consume these (pinned 1.0.68) — they are for the next
NuGet publish; keep them compiling and covered by SDK-level tests where cheap.

**Tests**: extend `tests/XcavateBuckets.Tests` with a stub `IIpfsService` (records calls,
returns fake CID) on a host mirroring `RestHost`; cover upload-happy-path, replace
semantics (second upload overwrites row), GET 404/200, unsigned POST → 400/401,
non-PDF → 400. Run `dotnet test tests/XcavateBuckets.Tests -c Release`.

## Component 3 — Mobile app (`/home/rosta/realXmarketMobileApp`)

### Routing

`XcavateAppShell.xaml` gains a `LawyerAccount` ShellContent → `LawyerMainPage` alongside
the existing `Account` → `InvestorMainPage`. At startup (`App.xaml.cs`, where the shell
is chosen :221-225) and on the bottom nav's Account action
(`XcavateNavigationBarViewModel`), route lawyers to `//LawyerAccount`. Lawyer detection:
on-chain `WhitelistModel.HasRoleAsync(address, XcavateRole.Lawyer, cluster, token)` with
the persisted `UserRoleEnum.Lawyer` as offline fallback; evaluated once per app start /
wallet change, cached in a small static (`LawyerRoleModel`).

### LawyerMainPage

`XcavateMobileApp/Pages/LawyerMainPage.xaml(.cs)` + `LawyerMainPageViewModel.cs`
(singleton, registered like the investor VM in `App.xaml.cs:214`). Mirrors the investor
page skeleton: root `AbsoluteLayout`, themed background, `RefreshView`+`ScrollView`,
same overlay chrome/popup block, `XcavateMainPageTopNavigationBarView`, bottom nav.

Content stack:

1. **Stats grid**: `XcavateCell` "Manage properties" bound to the under-instruction
   count (`totalCount` of the assigned query; tapping the cell activates the
   "Under Instruction" filter) + `solanacomponents:SolanaBalanceCellView` (unchanged).
2. **Terms document card** (`Border` card per `DefaultStyles` chrome): title "Terms
   document"; states — *none uploaded* ("Upload PDF" button), *uploaded* (file name /
   updated date + "View" and "Change" buttons), *busy* (activity indicator), *error*
   (message + retry, surfaced through the app's popup conventions). Upload uses
   `FilePicker.Default.PickAsync` with a PDF-only `FilePickerFileType`
   (Android/iOS/Windows variants), size-capped (25 MB), then posts through the new client.
3. **Filter row**: two `ElevatedButton`s — "Newly listed" (`NewlyListedActive`) and
   "Under Instruction" (`UnderInstructionActive`) — mutually exclusive toggles, same
   `ButtonStateEnum` pattern as the investor page. Default: Newly listed.
4. **Property list**: `BindableLayout` of `PropertyThumbnailView` over
   `ObservableCollection<XcavateNftWrapper>`, `ShowSkeleton` loading state, empty-state
   label, infinite scroll (`TryLoadMore…` at <280 px from bottom, `PageSize=20`).

The page XAML is added to the `<MauiXaml Update=… Generator="MSBuild:Compile">` list in
`XcavateMobileApp.csproj` (MauiXamlInflator=SourceGen requirement).

### Data layer

- New StrawberryShake query `LawyerProperties` in
  `realXmarketPlutoFramework/Indexers/XcavateDevnetIndexer/Queries/MarketplaceListings.graphql`
  selecting the existing `...ListingParts` fragment, with args
  `assignedLawyer`/`assignableTo` + `first`/`offset`. Local
  `Indexers/XcavateDevnetIndexer/schema.graphql` is updated to match the indexer change
  (same task controls both sides), then the client is regenerated with the pinned
  `strawberryshake.tools` (`dotnet graphql` in that folder).
- `XcavateMarketplaceIndexerModel` gains
  `GetLawyerAssignablePropertiesAsync(cluster, lawyer, first, offset, token)` and
  `GetLawyerAssignedPropertiesAsync(cluster, lawyer, first, offset, token)`, returning
  the same record shape the investor path wraps via
  `XcavatePropertyModel.ToXcavateNftWrapperAsync`.

### Terms document client (app side)

New `PlutoFramework/Model/Xcavate/Profile/TermsDocumentClient.cs` — hand-rolled static
client in the `RentCollectorSignatureClient` style (`new HttpClient`, base URL constant
`https://profile-api.xcavate.io/`):

- `GetAsync(address, ct)` → GET `api/profiles/{addr}/terms-document`; 404 → null.
- `UploadAsync(address, fileName, stream, ct)` → multipart POST with signed headers.
  Signing reuses the pinned package's primitives exactly as
  `SolanaAccountRequestSigner.cs` / `RealXmarketOfferClient.cs` do (X-SS58-Address /
  X-Signature / X-Timestamp; multipart signs `EmptyPayloadBody`; signer from
  `MainKeyModel.GetSignerAsync`). Non-success throws with the server body for the VM to
  surface.

### TermsDocumentPage

`XcavateMobileApp/Pages/TermsDocumentPage.xaml(.cs)` — title bar + `WebView` +
loading indicator + error state. Source selection: iOS/MacCatalyst load the IPFS gateway
URL directly (WKWebView renders PDFs); Android wraps it in the Google Docs embedded
viewer (`https://docs.google.com/viewer?embedded=true&url=<urlencoded gateway url>`),
since Android WebView won't render PDFs inline. No new NuGet dependencies. Navigation via
`NavigationModel.PushAsync` from the card's View action, passing the gateway URL.

## Error handling

- Indexer: invalid base58 in the new args → same error style as the existing `developer`
  arg (GraphQL error, no 500).
- ProfileApi: missing `PINATA_JWT` → 503; unsigned/bad-signature POST → 401; wrong
  content type/extension → 400; Pinata failure → 502 with message; oversized → 413 via
  request limits.
- App: upload failures and unreachable API surface via the existing popup/message
  conventions; card returns to its previous state. GET 404 is the normal "no document"
  state, never an error UI.

## Testing

- Indexer: new GraphQL tests (assigned-lawyer filter both slots, vacancy filter,
  exclusion of own-assigned, sold-out ordering, pagination counts). Needs
  `DATABASE_URL` Postgres per repo AGENTS.md (docker `carbon-mig-test-pg`).
- ProfileApi: NUnit tests in `tests/XcavateBuckets.Tests` as above; hermetic via stub
  `IIpfsService`.
- App: solution builds (`dotnet build XcavateMobileApp.sln` for Android target where the
  environment allows); no existing UI test harness — verification is build + manual
  reasoning against the investor page's proven patterns. PlutoFrameworkTests stay green.

## Risks / notes

- The app schema.graphql is a local copy — if the deployed indexer lags the app release,
  the new query fails server-side. Deploy order: indexer first, then app release.
- `sold_out_notifications` rows vanish on devnet resets; ordering degrades to slot order
  (`NULLS LAST`), acceptable.
- Pinata is the first IPFS dependency; credentials are ops-owned env vars. The
  `IIpfsService` seam keeps a future provider swap trivial.
