using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlutoFramework;
using PlutoFramework.Components.XcavateProperty;
using PlutoFramework.Model;
using PlutoFramework.Model.Currency;
using PlutoFramework.Model.Xcavate;
using PlutoFrameworkCore.Solana;
using PlutoFrameworkCore.Xcavate;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Numerics;
using UniqueryPlus.Nfts;
using NftKey = (UniqueryPlus.NftTypeEnum, System.Numerics.BigInteger, System.Numerics.BigInteger);
using PropertyModel = PlutoFramework.Model.Xcavate.XcavatePropertyModel;
using XcavatePropertyModel = PlutoFramework.Components.XcavateProperty.XcavatePropertyModel;

namespace XcavateMobileApp.Pages;

public partial class InvestorMainPageViewModel : ObservableObject
{
    private const int PageSize = 20;

    private readonly Dictionary<NftKey, XcavateNftWrapper> ownedPropertiesDict = [];
    private readonly object loadingLock = new();
    private readonly SemaphoreSlim loadMoreSemaphore = new(1, 1);
    private CancellationTokenSource? loadingCts;
    private bool clientLoaded;
    private string ownerAddress = string.Empty;
    private int offset;
    private bool hasMore = true;
    private bool isBackgroundHydrationRunning;
    private readonly PropertyMarketplaceFilterPopupViewModel filterPopupViewModel;
    private string includesTownCity = string.Empty;
    private string includesPropertyType = string.Empty;
    private string includesPropertyName = string.Empty;
    private bool filterActive = false;
    private string lastLoadedTownCity = string.Empty;
    private string lastLoadedPropertyType = string.Empty;
    private string lastLoadedPropertyName = string.Empty;
    private bool lastLoadedOwned;
    private bool lastLoadedBought;
    private bool hasLoadedQuery;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalTokensText))]
    private uint totalTokens;

    public string TotalTokensText => TotalTokens.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalInvestedText))]
    private double totalInvested;
    public string TotalInvestedText => ((double)TotalInvested).ToCurrencyString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoiText))]
    private double roi;
    public string RoiText => $"{Roi:P1}";

    public InvestorMainPageViewModel()
    {
        filterPopupViewModel = DependencyService.Get<PropertyMarketplaceFilterPopupViewModel>();
        filterPopupViewModel.ApplyRequested = ApplyFiltersAsync;
        filterPopupViewModel.CancelRequested = async () => await HandleFilterCancelAsync().ConfigureAwait(false);
        OwnedProperties.CollectionChanged += OnOwnedPropertiesCollectionChanged;

        // A confirmed marketplace transaction (a reserve, buy or claim) changes this
        // wallet's positions and the totals computed from them, so the page's figures are
        // pre-transaction ones. This view model is a session-wide singleton - like
        // SolanaBalanceCellView it has no disposal hook, so it never unsubscribes: the
        // static event can only keep a singleton alive.
        XcavateMarketplaceTransactionModel.TransactionConfirmed += OnMarketplaceTransactionConfirmed;

        // The owned list is read from the selected network's marketplace deployment, so a
        // network switch re-reads it. Same singleton lifetime and no-unsubscribe convention
        // as the subscription above.
        SolanaNetworkModel.ClusterChanged += OnSolanaClusterChanged;

        // Claim-all and the per-property "Claim rent" chips serialize through
        // ClaimRentModel; the button mirrors its busy state. Same singleton
        // no-unsubscribe convention as above.
        ClaimRentModel.ClaimInProgressChanged += OnClaimInProgressChanged;
    }

    private void OnClaimInProgressChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(UpdateClaimAllButton);

    /// <summary>
    /// Runs on the main thread - the event already is - and fire-and-forget: the re-fetch
    /// is pure async I/O, so nothing blocks the UI while it runs. RefreshAsync's own
    /// isRefreshInProgress guard absorbs a concurrent pull-to-refresh.
    /// </summary>
    private void OnMarketplaceTransactionConfirmed(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // A confirmed marketplace transaction can move rent (a claim pulled it, a
            // distribution added to it), so the cached income figures are
            // pre-transaction ones. Cleared and re-scanned; the card keeps showing the
            // old totals until the fresh scan lands - no fake zeroing.
            InvalidateIncomeScan();
            QueueIncomeLoad();
            _ = RefreshAsync(CancellationToken.None);
        });

    private void OnSolanaClusterChanged(object? sender, SolanaCluster cluster)
    {
        // The empty-state caption depends on the selected cluster, so swap it the moment the
        // switch lands; the list itself is re-read by the refresh.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            OnPropertyChanged(nameof(NoOwnedPropertiesMessage));

            // Income figures are per cluster (and wallet): the cache key would never
            // match again anyway, so drop the old cluster's entries up front.
            InvalidateIncomeScan();

            _ = RefreshAsync(CancellationToken.None);
        });
    }

    partial void OnOwnedActiveChanged(bool value)
    {
        OwnedButtonState = value ? PlutoFramework.Components.Buttons.ButtonStateEnum.Enabled : PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;
        if (value)
        {
            filterActive = false;
            hasLoadedQuery = false;
            filterPopupViewModel.SetToDefault();
            BoughtActive = false;
            BoughtButtonState = PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;
            _ = RestartOwnedPropertiesLoadAsync(CancellationToken.None);
        }
    }

    partial void OnBoughtActiveChanged(bool value)
    {
        BoughtButtonState = value ? PlutoFramework.Components.Buttons.ButtonStateEnum.Enabled : PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;
        if (value)
        {
            filterActive = false;
            hasLoadedQuery = false;
            filterPopupViewModel.SetToDefault();
            OwnedActive = false;
            OwnedButtonState = PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;
            _ = RestartOwnedPropertiesLoadAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    private void ToggleOwned()
    {
        OwnedActive = !OwnedActive;
    }

    [RelayCommand]
    private void ToggleBought()
    {
        BoughtActive = !BoughtActive;
    }

    [RelayCommand]
    private void OpenFilter()
    {
        OwnedActive = false;
        BoughtActive = false;
        OwnedButtonState = PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;
        BoughtButtonState = PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;

        // The popup view model is shared with the marketplace page; this page's
        // investorProperties query has no state filter, so the section stays hidden here.
        filterPopupViewModel.IsStateFilterVisible = false;
        filterPopupViewModel.IsVisible = true;
    }

    private async Task HandleFilterCancelAsync()
    {
        // When filter is cancelled, reset to default state and reload
        filterActive = false;
        hasLoadedQuery = false;
        filterPopupViewModel.SetToDefault();
        OwnedActive = false;
        BoughtActive = false;
        OwnedButtonState = PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;
        BoughtButtonState = PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;

        await RestartOwnedPropertiesLoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ApplyFiltersAsync()
    {
        includesTownCity = NormalizeFilterValue(filterPopupViewModel.SelectedTownCity);
        includesPropertyType = NormalizeFilterValue(filterPopupViewModel.SelectedPropertyType);
        includesPropertyName = filterPopupViewModel.SearchText?.Trim() ?? string.Empty;

        OwnedActive = false;
        BoughtActive = false;
        OwnedButtonState = PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;
        BoughtButtonState = PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;

        filterActive = true;

        if (!IsSameLoadedQuery(includesPropertyName, includesTownCity, includesPropertyType, OwnedActive, BoughtActive))
        {
            await RestartOwnedPropertiesLoadAsync(CancellationToken.None).ConfigureAwait(false);
            RememberLoadedQuery();
        }

        filterPopupViewModel.IsVisible = false;
    }

    private async Task RestartOwnedPropertiesLoadAsync(CancellationToken externalToken)
    {
        try
        {
            var token = ReplaceLoadingToken(externalToken);
            await LoadOwnedPropertiesForSelectedEndpointAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when user refreshes or leaves the page.
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private static string NormalizeFilterValue(string value)
    {
        return string.Equals(value, "All", StringComparison.OrdinalIgnoreCase) ? string.Empty : value;
    }

    [ObservableProperty]
    private bool isRefreshing;

    private bool isRefreshInProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoOwnedProperties))]
    private ObservableCollection<XcavateNftWrapper> ownedProperties = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoOwnedProperties))]
    [NotifyPropertyChangedFor(nameof(ShowSkeleton))]
    private bool ownedPropertiesLoading;

    public bool NoOwnedProperties => !OwnedPropertiesLoading && OwnedProperties.Count == 0;

    /// <summary>
    /// The empty-owned-list caption. A cluster with no marketplace deployment explains
    /// itself instead of implying the user's transactions are merely pending.
    /// </summary>
    public string NoOwnedPropertiesMessage =>
        !XcavateDeploymentModel.IsDeployed(SolanaNetworkModel.SelectedCluster)
            ? XcavateDeploymentModel.NotDeployedMessage(SolanaNetworkModel.SelectedCluster)
            : "Your purchased properties will appear here once your transactions are completed.";

    /// <summary>True while the list is still empty and loading (initial load / refresh).</summary>
    public bool ShowSkeleton => OwnedPropertiesLoading && OwnedProperties.Count == 0;

    [ObservableProperty]
    private bool ownedActive = false;

    [ObservableProperty]
    private bool boughtActive = false;

    [ObservableProperty]
    private PlutoFramework.Components.Buttons.ButtonStateEnum ownedButtonState = PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;

    [ObservableProperty]
    private PlutoFramework.Components.Buttons.ButtonStateEnum boughtButtonState = PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken externalToken)
    {
        // RefreshView sets IsRefreshing to true (TwoWay binding) before executing
        // RefreshCommand, so IsRefreshing cannot be used as a re-entrancy guard here.
        if (isRefreshInProgress)
        {
            return;
        }

        isRefreshInProgress = true;
        IsRefreshing = true;

        try
        {
            var token = ReplaceLoadingToken(externalToken);

            // A user-initiated refresh should always re-fetch, even for the same query.
            hasLoadedQuery = false;

            await LoadOwnedPropertiesForSelectedEndpointAsync(token).ConfigureAwait(false);

            await MainPageLayoutUpdater.ViewLocalLoadAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer refresh starts.
        }
        finally
        {
            IsRefreshing = false;
            isRefreshInProgress = false;
        }
    }

    public async Task LoadOwnedPropertiesForSelectedEndpointAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // The investor's positions live in the Xcavate Solana marketplace on the selected
        // network, read through that cluster's Xcavate indexer - the wallet that signs
        // there is the Solana key, not the Substrate one.
        var selectedOwnerAddress = KeysModel.GetSolanaAddress();

        if (string.IsNullOrWhiteSpace(selectedOwnerAddress))
        {
            ResetOwnedProperties();
            OwnedPropertiesLoading = false;
            return;
        }

        var shouldReload = !clientLoaded ||
                           !string.Equals(ownerAddress, selectedOwnerAddress, StringComparison.Ordinal) ||
                           !IsSameLoadedQuery(includesPropertyName, includesTownCity, includesPropertyType, OwnedActive, BoughtActive);

        if (!string.Equals(ownerAddress, selectedOwnerAddress, StringComparison.Ordinal))
        {
            // A different wallet has no claim on the previous wallet's cached income.
            MainThread.BeginInvokeOnMainThread(InvalidateIncomeScan);
        }

        clientLoaded = true;
        ownerAddress = selectedOwnerAddress;

        if (shouldReload)
        {
            ResetOwnedProperties();
            RememberLoadedQuery();
            await LoadMoreOwnedPropertiesAsync(token).ConfigureAwait(false);
            _ = HydrateRemainingOwnedPropertiesAsync(token);
        }
        else if (OwnedProperties.Count == 0 && hasMore)
        {
            await LoadMoreOwnedPropertiesAsync(token).ConfigureAwait(false);
            _ = HydrateRemainingOwnedPropertiesAsync(token);
        }

        // Scan the (new) list for claimable rent. Cached entries are reused, so a
        // pull-to-refresh costs an indexer read only for properties never scanned.
        QueueIncomeLoad();
    }

    public Task TryLoadMoreOwnedPropertiesAsync(CancellationToken token) => LoadMoreOwnedPropertiesAsync(token);

    public void CancelOngoingLoading()
    {
        lock (loadingLock)
        {
            loadingCts?.Cancel();
        }
    }

    private CancellationToken ReplaceLoadingToken(CancellationToken externalToken)
    {
        CancellationTokenSource? previousCts;
        CancellationTokenSource newCts;

        lock (loadingLock)
        {
            previousCts = loadingCts;
            newCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            loadingCts = newCts;
        }

        previousCts?.Cancel();
        previousCts?.Dispose();

        return newCts.Token;
    }

    private async Task HydrateRemainingOwnedPropertiesAsync(CancellationToken token)
    {
        if (isBackgroundHydrationRunning)
        {
            return;
        }

        isBackgroundHydrationRunning = true;

        try
        {
            while (hasMore)
            {
                token.ThrowIfCancellationRequested();
                await LoadMoreOwnedPropertiesAsync(token).ConfigureAwait(false);
            }

            // The income scan's first pass ran on the first page; queue another so the
            // properties hydration added since are covered too (cached entries skip).
            QueueIncomeLoad();
        }
        catch (OperationCanceledException)
        {
            // Expected when refresh starts again or page disappears.
        }
        finally
        {
            isBackgroundHydrationRunning = false;
        }
    }

    private async Task LoadMoreOwnedPropertiesAsync(CancellationToken token)
    {
        if (!hasMore || !clientLoaded || string.IsNullOrWhiteSpace(ownerAddress))
        {
            return;
        }

        token.ThrowIfCancellationRequested();

        // Positions come from the selected cluster's marketplace deployment; where there
        // is none the owned list stays empty rather than erroring.
        if (!XcavateDeploymentModel.IsDeployed(SolanaNetworkModel.SelectedCluster))
        {
            hasMore = false;
            return;
        }

        await loadMoreSemaphore.WaitAsync(token).ConfigureAwait(false);

        try
        {
            if (OwnedPropertiesLoading || !hasMore || !clientLoaded || string.IsNullOrWhiteSpace(ownerAddress))
            {
                return;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                OwnedPropertiesLoading = true;
            });

            // Every filter runs server-side (ADR-34): the toggles pick owned/reserved
            // positions, the search bar matches the property name or postcode, the
            // popup's dropdowns match town/city and property type - so every position
            // the indexer returns is one to show, and `offset` pages straight through
            // the filtered result set.
            var page = await XcavateMarketplaceIndexerModel.GetInvestorPropertiesAsync(
                    SolanaNetworkModel.SelectedCluster,
                    ownerAddress,
                    owned: OwnedActive ? true : null,
                    reserved: BoughtActive ? true : null,
                    name: string.IsNullOrEmpty(includesPropertyName) ? null : includesPropertyName,
                    townCity: string.IsNullOrEmpty(includesTownCity) ? null : includesTownCity,
                    propertyType: string.IsNullOrEmpty(includesPropertyType) ? null : includesPropertyType,
                    first: PageSize,
                    offset: offset,
                    token: token)
                .ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            if (page.Count == 0)
            {
                hasMore = false;
            }
            else
            {
                offset += page.Count;

                if (page.Count < PageSize)
                {
                    hasMore = false;
                }
            }

            var wrappedBatch = await Task.WhenAll(
                    page.Select(property => XcavatePropertyModel.ToXcavateNftWrapperAsync(property.Listing, token)))
                .ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            var newItems = new List<XcavateNftWrapper>();

            foreach (var wrappedProperty in wrappedBatch)
            {
                if (ownedPropertiesDict.ContainsKey(wrappedProperty.Key))
                {
                    continue;
                }

                ownedPropertiesDict[wrappedProperty.Key] = wrappedProperty;
                newItems.Add(wrappedProperty);
            }

            if (newItems.Count > 0)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    foreach (var wrappedProperty in newItems)
                    {
                        OwnedProperties.Add(wrappedProperty);
                    }

                    RecalculatePortfolioMetrics();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when user refreshes or leaves the page.
        }
        catch (Exception ex)
        {
            Console.WriteLine("Owned indexed properties list error:");
            Console.WriteLine(ex);
            hasMore = false;
        }
        finally
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                OwnedPropertiesLoading = false;
                OnPropertyChanged(nameof(NoOwnedProperties));
            });

            loadMoreSemaphore.Release();
        }
    }

    private void ResetOwnedProperties()
    {
        offset = 0;
        hasMore = true;
        isBackgroundHydrationRunning = false;
        ownedPropertiesDict.Clear();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            OwnedProperties.Clear();
            RecalculatePortfolioMetrics();
            OnPropertyChanged(nameof(NoOwnedProperties));
        });
    }

    private void OnOwnedPropertiesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(NoOwnedProperties));
        OnPropertyChanged(nameof(ShowSkeleton));
        OnPropertyChanged(nameof(RentCardIsVisible));
    }

    private void RecalculatePortfolioMetrics()
    {
        TotalTokens = (uint)OwnedProperties.Sum(x => x.TokensBought + x.TokensOwned);
        var totalInvested = OwnedProperties.Sum(x => (long)((x.TokensBought + x.TokensOwned) * ((INftXcavateMetadata)x.NftBase).XcavateMetadata?.Financials.PricePerToken ?? 0));
        TotalInvested = totalInvested;

        decimal totalIncome = 0m;
        decimal investedAtYieldScale = 0m;

        foreach (var property in OwnedProperties)
        {
            var financials = ((INftXcavateMetadata)property.NftBase).XcavateMetadata?.Financials;

            if (financials is null)
            {
                continue;
            }

            var shares = property.TokensBought + property.TokensOwned;
            var shareCount = PropertyModel.GetYieldShareCount(financials);

            if (shareCount > 0)
            {
                totalIncome += shares * (financials.EstimatedRentalIncome / shareCount);
            }

            // A yield ratio must not mix the document's rental income with chain-priced
            // figures: since SharePriceDecimals moved to 9 the two live in different
            // price scales. Both sides come from the document's own financials.
            investedAtYieldScale += shares * PropertyModel.GetYieldPricePerToken(financials);
        }

        Roi = investedAtYieldScale > 0 ? (double)(totalIncome / investedAtYieldScale) * 12 : 0;
    }

    private bool IsSameLoadedQuery(string searchText, string townCity, string propertyType, bool owned, bool bought)
    {
        return hasLoadedQuery
            && string.Equals(lastLoadedPropertyName, searchText ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(lastLoadedTownCity, townCity ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(lastLoadedPropertyType, propertyType ?? string.Empty, StringComparison.Ordinal)
            && lastLoadedOwned == owned
            && lastLoadedBought == bought;
    }

    private void RememberLoadedQuery()
    {
        lastLoadedPropertyName = includesPropertyName;
        lastLoadedTownCity = includesTownCity;
        lastLoadedPropertyType = includesPropertyType;
        lastLoadedOwned = OwnedActive;
        lastLoadedBought = BoughtActive;
        hasLoadedQuery = true;
    }

    // ── Rent available (income claims) ──

    /// <summary>One mint's claimable amount on one property, in the mint's BASE units.</summary>
    private sealed record ClaimableMint(string Mint, BigInteger Amount, int Decimals, string Symbol);

    /// <summary>One owned property's claimable rent as the last scan computed it.</summary>
    private sealed record ClaimableIncomeEntry(long AssetId, string PropertyName, IReadOnlyList<ClaimableMint> Claimables);

    /// <summary>
    /// Per-asset income scan results, keyed by (cluster, assetId, wallet) so a
    /// pull-to-refresh reuses them and only a wallet/cluster switch - or a confirmed
    /// marketplace transaction - invalidates. Mutated on the main thread only.
    /// </summary>
    private readonly Dictionary<(SolanaCluster Cluster, long AssetId, string Wallet), ClaimableIncomeEntry> claimableByProperty = [];

    /// <summary>Mint decimals by (cluster, mint), resolved once per mint per session.</summary>
    private readonly Dictionary<(SolanaCluster Cluster, string Mint), int> incomeMintDecimals = [];

    private readonly object incomeLoadLock = new();
    private bool incomeLoadRunning;
    private bool incomeLoadQueued;

    /// <summary>True while any scanned property shows claimable rent (main thread only).</summary>
    private bool hasClaimableRentTotal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RentCardIsVisible))]
    private bool rentLoadSettled;

    [ObservableProperty]
    private string rentTotalText = "";

    [ObservableProperty]
    private string claimAllButtonText = "Claim all";

    [ObservableProperty]
    private PlutoFramework.Components.Buttons.ButtonStateEnum claimAllButtonState =
        PlutoFramework.Components.Buttons.ButtonStateEnum.Disabled;

    /// <summary>
    /// The card stays hidden until the wallet owns anything; while the scan runs it
    /// shows progress, and once the scan settles it survives only if there is rent to
    /// claim - an all-zero result hides it rather than showing an empty card.
    /// </summary>
    public bool RentCardIsVisible => OwnedProperties.Count > 0 && (!RentLoadSettled || hasClaimableRentTotal);

    /// <summary>
    /// Drops every cached income figure and returns the card to its pre-scan state.
    /// Main thread only (the callers marshal).
    /// </summary>
    private void InvalidateIncomeScan()
    {
        claimableByProperty.Clear();
        hasClaimableRentTotal = false;
        RentTotalText = "";
        RentLoadSettled = false;
        OnPropertyChanged(nameof(RentCardIsVisible));
        UpdateClaimAllButton();
    }

    /// <summary>
    /// Schedules an income-scan pass. Passes never overlap: a request made while one is
    /// running is remembered and folded into a follow-up pass, so properties that
    /// arrived mid-pass are still scanned.
    /// </summary>
    private void QueueIncomeLoad()
    {
        lock (incomeLoadLock)
        {
            if (incomeLoadRunning)
            {
                incomeLoadQueued = true;
                return;
            }

            incomeLoadRunning = true;
        }

        _ = RunIncomeLoadLoopAsync();
    }

    private async Task RunIncomeLoadLoopAsync()
    {
        try
        {
            while (true)
            {
                await LoadClaimableRentPassAsync().ConfigureAwait(false);

                lock (incomeLoadLock)
                {
                    if (!incomeLoadQueued)
                    {
                        incomeLoadRunning = false;
                        break;
                    }

                    incomeLoadQueued = false;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Claimable-rent scan failed:");
            Console.WriteLine(ex);

            lock (incomeLoadLock)
            {
                incomeLoadRunning = false;
                incomeLoadQueued = false;
            }
        }

        // Settled either way: a failed scan must not pin the card on "Checking…"
        // forever; whatever did load is what the card shows.
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            RentLoadSettled = true;
            UpdateRentCard();
        });
    }

    /// <summary>
    /// One scan pass over the owned list: cached properties are re-applied to their
    /// (possibly rebuilt) thumbnails, unscanned ones are fetched from the indexer -
    /// income state and the holding's share count - at most three in flight. Each
    /// property's result lands on the card the moment it resolves, so totals fill in
    /// live. A property that fails to read is skipped (logged), keeping whatever it
    /// showed before.
    /// </summary>
    private async Task LoadClaimableRentPassAsync()
    {
        var cluster = SolanaNetworkModel.SelectedCluster;
        var wallet = ownerAddress;

        if (string.IsNullOrWhiteSpace(wallet) || !XcavateDeploymentModel.IsDeployed(cluster))
        {
            return;
        }

        var snapshot = await MainThread.InvokeOnMainThreadAsync(() => OwnedProperties.ToList());

        var targets = snapshot
            .Select(wrapper => (Wrapper: wrapper, Listing: wrapper.NftBase as XcavateSolanaListingNft))
            .Where(target => target.Listing is not null)
            .ToList();

        // Cached entries are reused - only their chip flags need re-applying, since the
        // list rebuild creates fresh wrapper instances on a wallet-cluster reload path.
        foreach (var target in targets)
        {
            if (claimableByProperty.TryGetValue((cluster, target.Listing!.AssetId, wallet), out var cached))
            {
                var wrapper = target.Wrapper;
                var hasClaimable = cached.Claimables.Count > 0;
                await MainThread.InvokeOnMainThreadAsync(() => wrapper.HasClaimableRent = hasClaimable);
            }
        }

        var unscanned = targets
            .Where(target => !claimableByProperty.ContainsKey((cluster, target.Listing!.AssetId, wallet)))
            .ToList();

        if (unscanned.Count == 0)
        {
            return;
        }

        var rpc = SolanaRpcModel.GetClient(cluster);
        var fetchGate = new SemaphoreSlim(3, 3);

        await Task.WhenAll(unscanned.Select(async target =>
        {
            await fetchGate.WaitAsync().ConfigureAwait(false);

            try
            {
                var listing = target.Listing!;
                var assetId = listing.AssetId;

                var income = await XcavateMarketplaceIndexerModel.GetIncomeStateAsync(
                        cluster, assetId, wallet, rpc, CancellationToken.None)
                    .ConfigureAwait(false);

                var governance = await XcavateMarketplaceIndexerModel.GetGovernanceStateAsync(
                        cluster, listing.ListingId, assetId, wallet, CancellationToken.None)
                    .ConfigureAwait(false);

                var shares = governance.Holding?.Amount ?? 0u;

                var claimables = new List<ClaimableMint>();

                foreach (var stream in income.Streams)
                {
                    var claimable = income.GetClaimable(stream.Mint, shares);

                    if (claimable.Sign <= 0)
                    {
                        continue;
                    }

                    var decimals = await ResolveIncomeMintDecimalsAsync(cluster, stream.Mint)
                        .ConfigureAwait(false);

                    claimables.Add(new ClaimableMint(stream.Mint, claimable, decimals, IncomeMintSymbol(cluster, stream.Mint)));
                }

                var entry = new ClaimableIncomeEntry(
                    assetId,
                    listing.XcavateMetadata?.PropertyName ?? $"property #{assetId}",
                    claimables);

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    claimableByProperty[(cluster, assetId, wallet)] = entry;
                    target.Wrapper.HasClaimableRent = claimables.Count > 0;
                    UpdateRentCard();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to scan claimable rent for asset {target.Listing!.AssetId}:");
                Console.WriteLine(ex);
            }
            finally
            {
                fetchGate.Release();
            }
        })).ConfigureAwait(false);
    }

    /// <summary>
    /// The mint's decimals for the claimable display: the whitelist entry when the app
    /// knows the mint, else the mint account on chain - the same resolution
    /// <see cref="XcavateMarketplaceCallsModel"/> applies to payment mints in the buy
    /// flow.
    /// </summary>
    private async Task<int> ResolveIncomeMintDecimalsAsync(SolanaCluster cluster, string mint)
    {
        if (incomeMintDecimals.TryGetValue((cluster, mint), out var decimals))
        {
            return decimals;
        }

        var (_, resolved) = await XcavateMarketplaceCallsModel.ResolveMintAsync(
            cluster, new Solnet.Wallet.PublicKey(mint), CancellationToken.None).ConfigureAwait(false);

        incomeMintDecimals[(cluster, mint)] = resolved;

        return resolved;
    }

    /// <summary>
    /// The whitelist symbol the buy flow prints payment amounts with; an income mint the
    /// app does not know displays as plain "tokens" rather than a base58 address.
    /// </summary>
    private static string IncomeMintSymbol(SolanaCluster cluster, string mint) =>
        SolanaTokenWhitelist.ForCluster(cluster)
            .FirstOrDefault(entry => entry.Mint == mint)?.Symbol ?? "tokens";

    /// <summary>Rebuilds the card's totals from the cache and re-evaluates its state. Main thread only.</summary>
    private void UpdateRentCard()
    {
        var cluster = SolanaNetworkModel.SelectedCluster;
        var wallet = ownerAddress;

        var totals = new Dictionary<string, ClaimableMint>();

        foreach (var entry in claimableByProperty)
        {
            if (entry.Key.Cluster != cluster || entry.Key.Wallet != wallet)
            {
                continue;
            }

            foreach (var claimable in entry.Value.Claimables)
            {
                if (totals.TryGetValue(claimable.Mint, out var total))
                {
                    totals[claimable.Mint] = total with { Amount = total.Amount + claimable.Amount };
                }
                else
                {
                    totals[claimable.Mint] = claimable;
                }
            }
        }

        hasClaimableRentTotal = totals.Count > 0;

        RentTotalText = totals.Count == 0
            ? (RentLoadSettled ? "" : "Checking owned properties…")
            : string.Join("\n", totals.Values.Select(total =>
                $"{XcavateReserveBalanceModel.Format(XcavateMarketplaceIndexerModel.PropertyIncomeState.ToDisplayAmount(total.Amount, total.Decimals))} {total.Symbol}"));

        OnPropertyChanged(nameof(RentCardIsVisible));
        UpdateClaimAllButton();
    }

    private void UpdateClaimAllButton()
    {
        if (ClaimRentModel.ClaimInProgress)
        {
            ClaimAllButtonText = "Claiming…";
            ClaimAllButtonState = PlutoFramework.Components.Buttons.ButtonStateEnum.Disabled;
            return;
        }

        ClaimAllButtonText = "Claim all";
        ClaimAllButtonState = RentLoadSettled && hasClaimableRentTotal
            ? PlutoFramework.Components.Buttons.ButtonStateEnum.Enabled
            : PlutoFramework.Components.Buttons.ButtonStateEnum.Disabled;
    }

    /// <summary>
    /// Claims the wallet's rent on every owned property that shows a claimable balance,
    /// strictly one property at a time (one status popup each, named for the property).
    /// A property whose claim fails keeps its unclaimed balance on the card; the
    /// pipeline's confirmation event re-scans and refreshes the totals.
    /// </summary>
    [RelayCommand]
    private async Task ClaimAllRentAsync()
    {
        var cluster = SolanaNetworkModel.SelectedCluster;
        var wallet = ownerAddress;

        var targets = claimableByProperty
            .Where(entry => entry.Key.Cluster == cluster && entry.Key.Wallet == wallet && entry.Value.Claimables.Count > 0)
            .Select(entry => new ClaimRentModel.ClaimTarget(entry.Value.AssetId, entry.Value.PropertyName))
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        await ClaimRentModel.ClaimAsync(targets).ConfigureAwait(false);
    }
}
