# Lawyer Main Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give lawyer users a dedicated main page in the Xcavate MAUI app — claimed-property count + balance cards, a terms-document PDF card (upload/view via IPFS through XcavateProfileApi), and a property list with "Newly listed" / "Under Instruction" filters backed by new Indexer query args.

**Architecture:** Three repos, one feature. Indexer (`/home/rosta/Indexer`, Rust + juniper): two new optional args on the existing `listings` GraphQL query, no DB migration. ProfileApi (`/home/rosta/XcavateProfileApi`, ASP.NET Core net10): first IPFS (Pinata) integration + new EF entity + signed upload/public GET endpoints. App (`/home/rosta/realXmarketMobileApp`): new page mirroring `InvestorMainPage`, hand-rolled HTTP client (precedent: `RentCollectorSignatureClient`), StrawberryShake query against the locally-updated schema.

**Tech Stack:** .NET MAUI (CommunityToolkit.Mvvm source generators), StrawberryShake 16, ASP.NET Core net10 + EF Core 10 (Npgsql), Rust + Carbon 0.12 + juniper + sqlx.

**Spec:** `docs/superpowers/specs/2026-10-17-lawyer-main-page-design.md`

## Global Constraints

- Indexer repo (`/home/rosta/Indexer`) law (its AGENTS.md): never commit/push `main` — branch `agent/20261017-lawyer-listing-filters` + PR; never `cargo fmt --all` (plain `cargo fmt` only); never hand-edit `crates/*-decoder*`; additive-only changes; verify with `cargo fmt --check`, `cargo clippy --workspace --all-targets -- -D warnings`, `SQLX_OFFLINE=true cargo build --workspace --locked`, `cargo test --workspace --locked` (needs `DATABASE_URL=postgres://postgres:test@localhost:54329/postgres`; start DB with `docker run -d --name carbon-mig-test-pg -e POSTGRES_PASSWORD=test -p 54329:5432 postgres:16` unless already running).
- ProfileApi: keep `Async` suffixes on action names (`SuppressAsyncSuffixInActionNames = false`); absent/empty env key = feature off with 503; signed requests validate manually via `ISignatureValidator`; multipart uploads sign `EmptyPayloadBody`.
- App: no new NuGet dependencies; new pages must be added to the `<MauiXaml Update=… Generator="MSBuild:Compile">` list in `XcavateMobileApp/XcavateMobileApp.csproj` (MauiXamlInflator=SourceGen); pinned `XcavateProfileApiClient` NuGet is 1.0.68 — new REST calls are hand-rolled clients under `PlutoFramework/Model/Xcavate/Profile/`.
- GraphQL arg names are the cross-repo contract, exactly: `assignedLawyer: String`, `assignableTo: String` (both base58 addresses; absent = filter off).
- Terms endpoint contract, exactly: route `api/profiles/{ss58address}/terms-document`; response JSON `{ "cid", "url", "fileName", "updatedAt" }`; POST = signed multipart field name `file`, PDF only; GET = public, 404 when none.
- Git: mobile app repo AND `realXmarketPlutoFramework/` is a git submodule — leave all app-side changes uncommitted in the working tree (both repos) for user review. Indexer changes go on the branch above with commits. ProfileApi changes stay uncommitted.

## Review Focus

1. **Android WebView cannot render PDFs** — the TermsDocumentPage must use the Google Docs embedded-viewer wrapper on Android, which requires the IPFS gateway URL to be publicly reachable (default Pinata public gateway). Pinned by the platform-conditional step in Task 8.
2. **Pinata misconfigured/unreachable at runtime** — user must see a clean error, never a crash: 503 when `PINATA_JWT` absent (test `upload_returns_503_when_ipfs_not_configured`, Task 4), 502 on pin failure (test `upload_returns_502_when_pinata_fails`, Task 4), and the app card returns to its previous state with an error popup (Task 9 step).
3. **Repeated uploads** — last write wins, exactly one row per wallet (test `second_upload_replaces_document`, Task 4).
4. **Listings without a `sold_out_notifications` row** (pre-0018 data, devnet wipe) — ordering must degrade gracefully via `NULLS LAST` (asserted in `listings_assignable_to_orders_by_sold_out_slot_desc`, Task 1, with a row lacking a notification).
5. **App released before indexer deploy** — the `LawyerProperties` operation then errors server-side; the ViewModel must catch GraphQL errors and show the empty/error state instead of crashing (Task 9 error-path step).

---

### Task 1: Indexer — lawyer filters on `listings`

Repo: `/home/rosta/Indexer`. Work on branch `agent/20261017-lawyer-listing-filters` (create from `main`).

**Files:**
- Modify: `crates/api/src/graphql/query.rs` (~594-617, `listings` field on `QueryRoot`)
- Modify: `crates/api/src/graphql/programs/marketplace.rs` (`listings` resolver ~1153-1355, both page SQL ~1197-1254 and count SQL ~1268-1306)
- Test: `crates/api/src/graphql/tests.rs` (seed helpers `seed_listing_row` :79-121, model test `listings_filter_by_feed_state_partitions_every_listing` :318)

**Interfaces:**
- Consumes: existing `VACANT_SEAT` sentinel (`crates/api/src/graphql/programs/derived.rs:24-26`, 32 zero bytes = unassigned lawyer slot); existing arg-parsing style of the `developer: String` arg.
- Produces: `listings(assignedLawyer: String, assignableTo: String, …)` — GraphQL-visible contract consumed by Task 5's schema update.

- [ ] **Step 1: Branch + failing tests**

`git checkout -b agent/20261017-lawyer-listing-filters` from `main`. Add to `tests.rs`:

- `listings_filter_by_assigned_lawyer_matches_both_slots` — seed listing L1 with `developer_lawyer = A`, L2 with `spv_lawyer = A`, L3 with both vacant; query `listings(assignedLawyer: A)` → exactly L1, L2; `totalCount` = 2.
- `listings_filter_by_assignable_to_returns_vacant_sold_out_only` — seed SOLD_OUT+vacant (in), SOLD_OUT+both slots filled (out), LISTED+vacant (out), SOLD_OUT+vacant but `legal_deadline` in the past (out) → only the first.
- `listings_assignable_to_excludes_lawyers_own_listings` — SOLD_OUT with `developer_lawyer = A`, spv vacant → excluded for `assignableTo: A`, included for `assignableTo: B`.
- `listings_assignable_to_orders_by_sold_out_slot_desc` — two in-filter listings with `sold_out_notifications` rows at slots 100/200, plus one in-filter listing with NO notification row → order: slot-200, slot-100, no-row last.

Extend `seed_listing_row` (or add a variant) to accept lawyer pubkeys/status/legal_deadline; insert notification rows directly with SQL.

- [ ] **Step 2: Run tests, verify they fail**

`export DATABASE_URL=postgres://postgres:test@localhost:54329/postgres && cargo test -p api listings_ -- --nocapture`
Expected: FAIL — `assignedLawyer`/`assignableTo` unknown args (GraphQL validation errors).

- [ ] **Step 3: Implement the args**

`QueryRoot.listings` gains `assigned_lawyer: Option<String>`, `assignable_to: Option<String>` (juniper camelCases). In the marketplace resolver: parse base58 → bytea with the same error style as the existing `developer` arg. Dynamic WHERE arms (page and count queries identically):

- `assigned_lawyer = $n` → `AND (l.developer_lawyer = $n OR l.spv_lawyer = $n)`
- `assignable_to = $m` → `AND l.status = 'SOLD_OUT' AND (l.legal_deadline = 0 OR l.legal_deadline > EXTRACT(EPOCH FROM now())::bigint) AND (l.developer_lawyer = '\x00…00'::bytea OR l.spv_lawyer = '\x00…00'::bytea) AND l.developer_lawyer <> $m AND l.spv_lawyer <> $m`

When `assignable_to` is present, add `LEFT JOIN sold_out_notifications sn ON sn.listing_id = l.listing_id` and order `sn.slot DESC NULLS LAST, l.slot DESC, l.pubkey ASC`; otherwise keep `l.slot DESC, l.pubkey ASC`.

- [ ] **Step 4: Tests pass + repo gauntlet**

`cargo test -p api listings_` PASS, then per AGENTS.md: `cargo fmt && cargo fmt --check`, `cargo clippy --workspace --all-targets -- -D warnings`, `SQLX_OFFLINE=true cargo build --workspace --locked`, `(cd crates/api && cargo sqlx prepare --check -- --bin api)` (drop `--check` and commit `.sqlx/` if it complains), full `cargo test --workspace --locked`.

- [ ] **Step 5: Commit + PR**

Commit on the branch (`feat(api): lawyer filters on listings query`). Push and open a PR with `gh pr create` if available/authenticated; otherwise report the branch for manual PR. Do NOT merge.

---

### Task 2: ProfileApi — IPFS service

Repo: `/home/rosta/XcavateProfileApi`. Changes stay uncommitted.

**Files:**
- Create: `src/XcavateProfileApi/Services/Ipfs/IIpfsService.cs`
- Create: `src/XcavateProfileApi/Services/Ipfs/PinataIpfsService.cs`
- Create: `src/XcavateProfileApi/Services/Ipfs/NullIpfsService.cs`
- Modify: `src/XcavateProfileApi/Program.cs` (register next to the S3 block :122-133)
- Modify: `.env.example`, `docker-compose.yml` (document `PINATA_JWT`, `IPFS_GATEWAY_URL`)

**Interfaces:**
- Produces (consumed by Task 4):
  - `public interface IIpfsService { Task<string> PinFileAsync(Stream content, string fileName, string contentType, CancellationToken ct); string BuildGatewayUrl(string cid); }`
  - `public sealed class IpfsNotConfiguredException : Exception`
  - DI: `IIpfsService` singleton, always registered (Null when unconfigured).

- [ ] **Step 1: Interface + Pinata implementation**

`PinataIpfsService(HttpClient, jwt, gatewayBaseUrl)`: multipart POST to `https://api.pinata.cloud/pinning/pinFileToIPFS` with `Authorization: Bearer <jwt>`; file part named `file`; parse response JSON `IpfsHash` → return CID; non-success → throw `HttpRequestException` including response body. `BuildGatewayUrl` = `gatewayBaseUrl.TrimEnd('/') + "/" + cid`. `NullIpfsService.PinFileAsync` throws `IpfsNotConfiguredException`.

- [ ] **Step 2: DI + config**

In `Program.cs` beside the S3 registration: read `PINATA_JWT` / `IPFS_GATEWAY_URL` (default `https://gateway.pinata.cloud/ipfs/`); empty JWT → `NullIpfsService`. Add both keys to `.env.example` (blank values + comment) and the `environment:` list of `docker-compose.yml` following the existing entries' style.

- [ ] **Step 3: Build**

`dotnet build src/XcavateProfileApi/XcavateProfileApi.csproj` — 0 errors.

---

### Task 3: ProfileApi — `LawyerTermsDocument` entity + migration

**Files:**
- Create: `src/XcavateProfileApi/Models/LawyerTermsDocument.cs` (EF entity, API-internal)
- Modify: `src/XcavateProfileApi/Data/ProfileDbContext.cs` (new `DbSet`, key config mirroring `Profile` :40)
- Create: migration via CLI

**Interfaces:**
- Produces: `LawyerTermsDocument { string Ss58Address (PK); string Cid; string FileName; string ContentType; DateTimeOffset UpdatedAt }`; `ProfileDbContext.LawyerTermsDocuments`.

- [ ] **Step 1: Entity + DbSet**

Entity in the shape above (all strings required; `Ss58Address` is the PK — one document per wallet, last write wins). Register `DbSet<LawyerTermsDocument>` and `HasKey(d => d.Ss58Address)` in `OnModelCreating`.

- [ ] **Step 2: Migration**

`dotnet ef migrations add AddLawyerTermsDocuments --project src/XcavateProfileApi` (startup auto-applies with retry, `Program.cs:186-221`). Inspect the generated migration: one `CreateTable`, nothing else.

- [ ] **Step 3: Build**

`dotnet build src/XcavateProfileApi` — 0 errors.

---

### Task 4: ProfileApi — `TermsDocumentController` + SDK methods + tests

**Files:**
- Create: `src/XcavateProfileApi/Controllers/TermsDocumentController.cs`
- Create: `src/XcavateProfileApi/Models/TermsDocumentResponse.cs` — `{ Cid, Url, FileName, UpdatedAt }` with `[JsonPropertyName]` camelCase, mirroring `Models/RentCollectorSignatureResponse.cs`
- Modify: `src/XcavateProfileApiClient/XcavateProfileClient.cs` — add `GetTermsDocumentAsync` / `UploadTermsDocumentAsync` following `UploadImageAsync` (:174-207, signs `EmptyPayloadBody.Instance`) and `SendSignedAsync` (:406)
- Test: `tests/XcavateBuckets.Tests/TermsDocumentEndpointTests.cs` + extend `tests/XcavateBuckets.Tests/RestHost.cs` with a `StubIpfsService` (records uploads, returns CID `"bafy…test"`, `BuildGatewayUrl` → `https://gateway.test/ipfs/<cid>`) mirroring `StubS3Service` (:139-157)

**Interfaces:**
- Consumes: `IIpfsService`/`IpfsNotConfiguredException` (Task 2); `ProfileDbContext.LawyerTermsDocuments` (Task 3); `ISignatureValidator.ValidateAsync(address, signature, timestamp, method, lowercasePath, IPayloadBody)` and owner-or-admin 403 pattern (`ProfilesController.cs:359-437` and :204-210); `[SignedRequest]` doc attribute (`Swagger/SignatureSwagger.cs:15-16`); size-limit pattern `ImageUploads.RequestSizeLimit` = 26 MB (`Controllers/ImageUploads.cs:18`).
- Produces: the endpoint contract in Global Constraints; SDK methods `Task<TermsDocumentResponse?> GetTermsDocumentAsync(string ss58address, CancellationToken)` and `Task<TermsDocumentResponse> UploadTermsDocumentAsync(string ss58address, string fileName, Stream content, CancellationToken)`.

- [ ] **Step 1: Failing tests**

`TermsDocumentEndpointTests` (NUnit, host per `RestHost`, SDK client via `host.NewSdkClient()` so client/server signing agreement is exercised):
- `upload_pdf_returns_cid_and_persists_row`
- `get_returns_404_when_none`
- `get_returns_document_after_upload`
- `second_upload_replaces_document` (row count stays 1, CID updated)
- `upload_without_signature_headers_rejected` (401/400)
- `upload_non_pdf_rejected` (400; e.g. `.png` bytes)
- `upload_by_other_wallet_forbidden` (403; signed by B for route address A)
- `upload_returns_503_when_ipfs_not_configured` (host wired with `NullIpfsService`)
- `upload_returns_502_when_pinata_fails` (stub throws `HttpRequestException`)

- [ ] **Step 2: Run, verify fail** — `dotnet test tests/XcavateBuckets.Tests -c Release --filter TermsDocument` → compile/404 failures.

- [ ] **Step 3: Implement**

`TermsDocumentController`, `[Route("api/profiles/{ss58address}/terms-document")]`:
- `POST` (`UploadTermsDocumentAsync`): `[SignedRequest]`, `[RequestSizeLimit]` mirroring `ImageUploads`, `IFormFile file`; validate the three signature headers + route address == header address + owner-or-admin exactly as `ProfilesController.UploadImageAsync`; require `.pdf` extension AND `application/pdf` content type (own constants in the controller — do NOT modify `ImageUploads`); `PinFileAsync`; upsert via `FindAsync` (update or add, stamp `UpdatedAt`); map `IpfsNotConfiguredException` → 503, `HttpRequestException` → 502 with message; return `Ok(response)` with `Url = ipfs.BuildGatewayUrl(cid)`.
- `GET` (`GetTermsDocumentAsync`): public; `FindAsync`; null → `NotFound()`; else `Ok(response)`.
- SDK: response model + the two client methods.

- [ ] **Step 4: Tests pass** — `dotnet test tests/XcavateBuckets.Tests -c Release` (full suite green, not just the new tests).

---

### Task 5: App — GraphQL schema + `LawyerProperties` query + indexer model

Repo: `/home/rosta/realXmarketMobileApp`. Note `realXmarketPlutoFramework/` is a submodule; leave changes uncommitted.

**Files:**
- Modify: `realXmarketPlutoFramework/Indexers/XcavateDevnetIndexer/schema.graphql` (add `assignedLawyer: String` + `assignableTo: String` to the `listings` field, matching Task 1 exactly)
- Modify: `realXmarketPlutoFramework/Indexers/XcavateDevnetIndexer/Queries/MarketplaceListings.graphql` (new operation)
- Modify: `realXmarketPlutoFramework/PlutoFrameworkCore/Xcavate/XcavateMarketplaceIndexerModel.cs` (new methods near `GetInvestorPropertiesAsync` :1377)

**Interfaces:**
- Consumes: Task 1's GraphQL contract; existing `...ListingParts` fragment and `GetInvestorPropertiesAsync` mapping conventions.
- Produces (consumed by Task 9):
  - `Task<(int TotalCount, IReadOnlyList<ListingItem> Items)> GetLawyerAssignablePropertiesAsync(SolanaCluster cluster, string lawyer, int first, int offset, CancellationToken token)`
  - same signature for `GetLawyerAssignedPropertiesAsync(…)` — `ListingItem` = whatever listing record shape `XcavatePropertyModel.ToXcavateNftWrapperAsync` accepts (mirror the investor path; if the generated types differ, add a thin mapper — keep the GraphQL selection identical to the investor query's listing selection so fields line up).

- [ ] **Step 1: Schema + operation**

Hand-edit `schema.graphql`'s `listings` field (source of truth until the indexer deploys; Task 1 defines the real server shape). Add to `MarketplaceListings.graphql`:

```graphql
query LawyerProperties($assignedLawyer: String, $assignableTo: String, $first: Int, $offset: Int) {
  listings(assignedLawyer: $assignedLawyer, assignableTo: $assignableTo, first: $first, offset: $offset) {
    totalCount
    nodes { ...ListingParts }
  }
}
```

- [ ] **Step 2: Regenerate + build the indexer client project**

`dotnet build realXmarketPlutoFramework/Indexers/XcavateDevnetIndexer/XcavateDevnetIndexer.csproj` (StrawberryShake MSBuild regen) → 0 errors; confirm generated `LawyerProperties` operation exists.

- [ ] **Step 3: Model methods**

Both methods resolve the cluster client via `XcavateWhitelistIndexer.GetClient(cluster)`, execute `LawyerProperties` with one arg set and the other null, `EnsureNoErrors()`, map nodes exactly like `GetInvestorPropertiesAsync` does, and return `(totalCount, items)`.

- [ ] **Step 4: Build + existing tests**

`dotnet build realXmarketPlutoFramework/PlutoFrameworkCore` and `dotnet test realXmarketPlutoFramework/PlutoFrameworkTests` — green.

---

### Task 6: App — `TermsDocumentClient`

**Files:**
- Create: `realXmarketPlutoFramework/PlutoFramework/Model/Xcavate/Profile/TermsDocumentClient.cs`
- Test: `realXmarketPlutoFramework/PlutoFrameworkTests/` (add a parsing test next to existing model tests)

**Interfaces:**
- Consumes: signing mechanics of the pinned package as used by `SolanaAccountRequestSigner.cs` and `RealXmarketOfferClient.cs` (same folder); wallet signer via `MainKeyModel.GetSignerAsync`.
- Produces (consumed by Task 9):
  - `public record TermsDocumentInfo(string Cid, string Url, string FileName, DateTimeOffset UpdatedAt);`
  - `public static class TermsDocumentClient` with `Task<TermsDocumentInfo?> GetAsync(string address, CancellationToken ct)` (404 → null, other non-success → throw with body) and `Task<TermsDocumentInfo> UploadAsync(string address, string fileName, Stream pdf, CancellationToken ct)` (signed multipart POST; non-success → throw with body)
  - `public static TermsDocumentInfo Parse(string json)` — pure, unit-tested.

- [ ] **Step 1: Failing parsing test** — `Parse` maps `{cid,url,fileName,updatedAt}` JSON to the record; run `dotnet test realXmarketPlutoFramework/PlutoFrameworkTests --filter TermsDocument` → FAIL (type missing).

- [ ] **Step 2: Implement** — base URL constant `https://profile-api.xcavate.io/`; GET = plain `HttpClient`; POST = `MultipartFormDataContent` with `StreamContent` part named `file` (`application/pdf`), headers `X-SS58-Address`/`X-Signature`/`X-Timestamp` computed with the pinned package's signing helpers over `EmptyPayloadBody` — copy the exact signing sequence from `RealXmarketOfferClient.cs`.

- [ ] **Step 3: Test passes + build** — filtered test PASS; `dotnet build realXmarketPlutoFramework/PlutoFramework` 0 errors.

---

### Task 7: App — lawyer role detection + shell routing

**Files:**
- Create: `realXmarketPlutoFramework/PlutoFramework/Model/Xcavate/LawyerRoleModel.cs`
- Modify: `XcavateMobileApp/XcavateAppShell.xaml` (new ShellContent)
- Modify: `XcavateMobileApp/App.xaml.cs` (:214 VM registration area for Task 9; :221-225 shell-selection area here)
- Modify: `realXmarketPlutoFramework/PlutoFramework/Components/Xcavate/XcavateNavigationBarViewModel.cs` (:39)

**Interfaces:**
- Consumes: `WhitelistModel.HasRoleAsync(address, XcavateRole.Lawyer, cluster, token)` (`PlutoFrameworkCore/Xcavate/WhitelistModel.cs:112`); `KeysModel.GetSolanaAddress()`; persisted user role (`UserRoleEnum.Lawyer`, `XcavateUserModel.cs`); `SolanaNetworkModel.ClusterChanged`.
- Produces: `public static class LawyerRoleModel { static Task<bool> IsLawyerAsync(SolanaCluster cluster, CancellationToken ct); } }` — cached per (address, cluster), invalidated on `ClusterChanged`; on query failure falls back to persisted `UserRoleEnum.Lawyer`. Shell route `//LawyerAccount`.

- [ ] **Step 1: `LawyerRoleModel`** as specified above.

- [ ] **Step 2: Shell + navigation**

`XcavateAppShell.xaml`: add `<ShellContent Route="LawyerAccount" ContentTemplate="{DataTemplate pages:LawyerMainPage}" />` beside `Account` (same `Shell.PresentationMode` conventions). In `App.xaml.cs` where the root shell is assigned (:221-225): after assignment, `if (await LawyerRoleModel.IsLawyerAsync(...)) await Shell.Current.GoToAsync("//LawyerAccount");`. In `XcavateNavigationBarViewModel`, the Account action navigates to `//LawyerAccount` when `IsLawyerAsync` is true, else `//Account` (fire the check through the cached model, not a fresh query per tap).

- [ ] **Step 3: Build** — `dotnet build XcavateMobileApp/XcavateMobileApp.csproj` (Android TFM) 0 errors. LawyerMainPage doesn't exist until Task 9 — use a temporary minimal page or sequence this task's build step after Task 9's page creation; do not ship a half-wired shell alone.

---

### Task 8: App — `TermsDocumentPage` (PDF viewer)

**Files:**
- Create: `XcavateMobileApp/Pages/TermsDocumentPage.xaml` + `.xaml.cs`
- Modify: `XcavateMobileApp/XcavateMobileApp.csproj` (MauiXaml entry)

**Interfaces:**
- Consumes: `NavigationModel.PushAsync(Page)` (`PlutoFramework/Model/NavigationModel.cs:71`); page chrome conventions from other pushed pages (e.g. `PropertyDetailPage`).
- Produces: `public TermsDocumentPage(string title, string gatewayUrl)` — constructed and pushed by Task 9's View action.

- [ ] **Step 1: Page**

Layout: top bar with back button + title; `WebView` filling the rest; `ActivityIndicator` overlay while loading; error `Label` + retry button when the WebView navigation fails. Code-behind sets the source:

```csharp
#if ANDROID
    var src = "https://docs.google.com/viewer?embedded=true&url=" + Uri.EscapeDataString(gatewayUrl);
#else
    var src = gatewayUrl; // WKWebView renders PDFs natively
#endif
```

Back button pops via `Navigation.PopAsync` (or the house pattern); no new packages.

- [ ] **Step 2: csproj entry + build** — add the `MauiXaml Update` line; `dotnet build` 0 errors.

---

### Task 9: App — `LawyerMainPage` + ViewModel

**Files:**
- Create: `XcavateMobileApp/Pages/LawyerMainPage.xaml` + `.xaml.cs`
- Create: `XcavateMobileApp/Pages/LawyerMainPageViewModel.cs`
- Modify: `XcavateMobileApp/App.xaml.cs` (singleton registration beside :214)
- Modify: `XcavateMobileApp/XcavateMobileApp.csproj` (MauiXaml entry)

**Interfaces:**
- Consumes: Task 5's model methods; Task 6's `TermsDocumentClient`; Task 7's `//LawyerAccount` route; Task 8's `TermsDocumentPage`; `XcavatePropertyModel.ToXcavateNftWrapperAsync`; investor page's visual building blocks (`XcavateCell`, `SolanaBalanceCellView`, `ElevatedButton` with `ButtonStateEnum`, `PropertyThumbnailView`, `SkeletonCardView`, overlay popup block).
- Produces: the page behind route `//LawyerAccount`.

- [ ] **Step 1: Page XAML + code-behind**

Copy `InvestorMainPage.xaml`'s skeleton (root `AbsoluteLayout`, themed background, `RefreshView`+`ScrollView`, overlay chrome/popup block, top nav bar, bottom nav). Content stack replaces the investor stats/rent/filter blocks with:

1. Two-column grid: `xcavate:XcavateCell` titled "Manage properties" showing `AssignedCount` (tap → `ShowUnderInstructionCommand`) + `solanacomponents:SolanaBalanceCellView`.
2. Terms card (`Border` with `CardBackgroundColorLight/Dark`, `CardBorderColor`, `CardCornerRadius`): header "Terms document"; state views — none ("Upload PDF" button), uploaded (file name + `UploadedAt` date, "View" + "Change" buttons), busy (`ActivityIndicator`), error (message + "Try again").
3. Filter row: two `buttons:ElevatedButton` ("Newly listed" → `ToggleNewlyListedCommand`/`NewlyListedButtonState`, "Under Instruction" → `ToggleUnderInstructionCommand`/`UnderInstructionButtonState`), `HeightRequest="36" CornerRadius="18"`.
4. `BindableLayout.ItemsSource="{Binding LawyerProperties}"` of `PropertyThumbnailView`, `SkeletonCardView` (`ShowSkeleton`), empty-state `Label` (`EmptyListMessage`).

Code-behind mirrors the investor code-behind: VM via `DependencyService`, `OnLoaded` refresh with `CancellationTokenSource`, `OnMainScrollViewScrolled` → `TryLoadMoreAsync` at <280 px, `OnBackButtonPressed` → `PopupManager.TryCloseTopPopup()`. Do not implement `IPlutoFrameworkMainPage`'s balance list unless the copied chrome requires it — check `MainPageLayoutUpdater` usage and copy the investor page's approach verbatim.

- [ ] **Step 2: ViewModel**

`ObservableObject` singleton. State: `AssignedCount`, `NewlyListedActive`/`UnderInstructionActive` (+`*ButtonState`), `ObservableCollection<XcavateNftWrapper> LawyerProperties`, `ShowSkeleton`, `EmptyListMessage`, `TermsDocument` (`TermsDocumentInfo?`), `TermsBusy`, `TermsError`. Commands: `RefreshCommand` (terms info + assigned count via `GetLawyerAssignedPropertiesAsync(first:1)` totalCount + restart list), the two toggles (mutually exclusive, reset paging, default Newly listed), `ShowUnderInstructionCommand` (from the count card), `UploadTermsCommand` (`FilePicker.Default.PickAsync` with per-platform PDF `FilePickerFileType`, reject >25 MB, then `TermsDocumentClient.UploadAsync`; on error restore previous card state and show the message via the page's popup conventions), `ViewTermsCommand` (`NavigationModel.PushAsync(new TermsDocumentPage("Terms document", TermsDocument.Url))`). Paging mirrors the investor VM: `PageSize=20`, offset cursor, dedup dictionary keyed on `(NftTypeEnum, BigInteger, BigInteger)`, background hydration optional (skip it — lawyer lists are short). GraphQL/HTTP failures: catch, set empty state + error message (Review Focus #5), never crash.

- [ ] **Step 3: Registration + build**

Register the VM singleton in `App.xaml.cs`; add csproj entries; `dotnet build XcavateMobileApp/XcavateMobileApp.csproj` 0 errors, and `dotnet build XcavateMobileApp.sln` if the environment supports the full solution.

---

### Task 10: End-to-end verification + docs

- [ ] **Step 1:** Indexer PR checks green (fmt/clippy/build/test per Task 1 Step 4).
- [ ] **Step 2:** ProfileApi full suite green (`dotnet test tests/XcavateBuckets.Tests -c Release`) and `dotnet build` of the whole `XcavateProfile.sln`.
- [ ] **Step 3:** App builds (`XcavateMobileApp.csproj`) and `dotnet test realXmarketPlutoFramework/PlutoFrameworkTests` green.
- [ ] **Step 4:** Update `docs/lawyers-feature-notes/services-findings.md` §3 (strike the verified items this feature closed: terms upload exists, lawyer queries exist) with one-line notes + file references.
- [ ] **Step 5:** Report: indexer PR link/branch, uncommitted diffs in app + ProfileApi repos, how to set `PINATA_JWT`, and deploy-order note (indexer before app release).
