using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlutoFramework;
using PlutoFramework.Model;
using PlutoFramework.Model.Xcavate;
using PlutoFrameworkCore.Solana;
using PlutoFrameworkCore.Xcavate;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using NftKey = (UniqueryPlus.NftTypeEnum, System.Numerics.BigInteger, System.Numerics.BigInteger);
using XcavatePropertyModel = PlutoFramework.Components.XcavateProperty.XcavatePropertyModel;
using DeveloperPropertyFilter = PlutoFramework.Model.Xcavate.XcavateMarketplaceIndexerModel.DeveloperPropertyFilter;

namespace XcavateMobileApp.Pages;

/// <summary>
/// The developer landing page: two figure cells - "Active listed" (the wallet's listings
/// currently on the market) and "Properties sold" (those through completion) - next to the
/// Solana balance cell, above a property list whose five lifecycle filters - All, Pending,
/// For sale, Sold and Ended - all page through the indexer's single <c>DeveloperProperties</c>
/// query.
/// </summary>
/// <remarks>
/// Singleton, like <see cref="LawyerMainPageViewModel"/> - registered in App.xaml.cs and never
/// unsubscribed from the static events it listens to.
///
/// The list is the developer's own creations, matched on the listing's
/// <c>developer</c> field: the wallet is read from <see cref="KeysModel"/>, so a property is
/// theirs because they created it. The web page resolves the same thing through a company
/// record first, because its database keys properties on <c>companyId</c>; the indexer needs
/// no such step.
///
/// Failures from the indexer land in the empty/error states, never in a crash: the deployed
/// indexer may predate the <c>developer</c>/<c>statuses</c> filters, and a cluster with no
/// marketplace deployment simply has nothing to show. Both read as an empty page with an
/// explanation, not an exception.
/// </remarks>
public partial class DeveloperMainPageViewModel : ObservableObject
{
    private const int PageSize = 20;

    private readonly Dictionary<NftKey, XcavateNftWrapper> propertiesDict = [];
    private readonly object loadingLock = new();
    private readonly SemaphoreSlim loadMoreSemaphore = new(1, 1);
    private CancellationTokenSource? loadingCts;
    private string developerAddress = string.Empty;
    private int offset;
    private bool hasMore = true;
    private bool listLoadFailed;

    public DeveloperMainPageViewModel()
    {
        DeveloperProperties.CollectionChanged += OnDeveloperPropertiesCollectionChanged;

        // A network switch re-reads the list from the newly selected cluster's indexer.
        // Singleton lifetime: never unsubscribed, same convention as the lawyer page.
        SolanaNetworkModel.ClusterChanged += OnSolanaClusterChanged;
    }

    // ── List state ──

    [ObservableProperty]
    private bool isRefreshing;

    private bool isRefreshInProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoProperties))]
    private ObservableCollection<XcavateNftWrapper> developerProperties = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoProperties))]
    [NotifyPropertyChangedFor(nameof(ShowSkeleton))]
    private bool propertiesLoading;

    public bool NoProperties => !PropertiesLoading && DeveloperProperties.Count == 0;

    /// <summary>True while the list is still empty and loading (initial load / refresh).</summary>
    public bool ShowSkeleton => PropertiesLoading && DeveloperProperties.Count == 0;

    /// <summary>
    /// The empty-list caption: a cluster with no marketplace deployment explains itself, a
    /// failed load offers a retry, and each filter names its own empty state - including the
    /// two filters the web page has no counterpart for, which say so rather than looking
    /// broken.
    /// </summary>
    public string EmptyListMessage =>
        !XcavateDeploymentModel.IsDeployed(SolanaNetworkModel.SelectedCluster)
            ? XcavateDeploymentModel.NotDeployedMessage(SolanaNetworkModel.SelectedCluster)
            : listLoadFailed
                ? "The properties could not be loaded. Pull down to try again."
                : string.IsNullOrWhiteSpace(developerAddress)
                    ? "Sign in with your wallet to see the properties you created."
                    : ActiveFilter switch
                    {
                        DeveloperPropertyFilter.Pending => "Properties you have created but not listed yet will appear here.",
                        DeveloperPropertyFilter.ForSale => "None of your properties is on the market right now.",
                        DeveloperPropertyFilter.Sold => "Properties you have sold will appear here.",
                        DeveloperPropertyFilter.Ended => "None of your properties ended without completing.",
                        _ => "Properties you create will appear here.",
                    };

    // ── Figures ──
    //
    // The web page shows four figures here; only these three have a source the app can
    // actually read. "Sale revenue" is omitted rather than faked: the indexer offers no
    // aggregate over a developer's completed sales, and summing a fetched page of listings
    // would quietly misreport it. "Balance" is the Solana balance cell, which reads the
    // wallet itself.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveListedText))]
    private int activeListedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoldCountText))]
    private int soldCount;

    public string ActiveListedText => ActiveListedCount.ToString();

    public string SoldCountText => SoldCount.ToString();

    /// <summary>
    /// The list's one active lifecycle filter. Exactly one is always on: the query needs its
    /// filter argument decided, so the pills replace each other rather than toggling off.
    /// </summary>
    [ObservableProperty]
    private DeveloperPropertyFilter activeFilter = DeveloperPropertyFilter.All;

    // ── Filter pills ──

    [ObservableProperty]
    private PlutoFramework.Components.Buttons.ButtonStateEnum allButtonState =
        PlutoFramework.Components.Buttons.ButtonStateEnum.Enabled;

    [ObservableProperty]
    private PlutoFramework.Components.Buttons.ButtonStateEnum pendingButtonState =
        PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;

    [ObservableProperty]
    private PlutoFramework.Components.Buttons.ButtonStateEnum forSaleButtonState =
        PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;

    [ObservableProperty]
    private PlutoFramework.Components.Buttons.ButtonStateEnum soldButtonState =
        PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;

    [ObservableProperty]
    private PlutoFramework.Components.Buttons.ButtonStateEnum endedButtonState =
        PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;

    /// <summary>
    /// Keeps the five pills' visual state in step with the active filter, and restarts the
    /// list whenever it changes. Exactly one filter is always on - switching is a replacement,
    /// never a toggle off, the same rule the lawyer page's two filters follow.
    /// </summary>
    partial void OnActiveFilterChanged(DeveloperPropertyFilter value)
    {
        AllButtonState = State(value, DeveloperPropertyFilter.All);
        PendingButtonState = State(value, DeveloperPropertyFilter.Pending);
        ForSaleButtonState = State(value, DeveloperPropertyFilter.ForSale);
        SoldButtonState = State(value, DeveloperPropertyFilter.Sold);
        EndedButtonState = State(value, DeveloperPropertyFilter.Ended);

        _ = RestartPropertiesLoadAsync(CancellationToken.None);
    }

    private static PlutoFramework.Components.Buttons.ButtonStateEnum State(
        DeveloperPropertyFilter active,
        DeveloperPropertyFilter candidate) =>
        active == candidate
            ? PlutoFramework.Components.Buttons.ButtonStateEnum.Enabled
            : PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;

    // Async so each command satisfies XcavateCell's IAsyncRelayCommand as well as
    // ElevatedButton's ICommand - the two figure cells and the five pills share these.
    [RelayCommand]
    private Task SelectAllAsync()
    {
        ActiveFilter = DeveloperPropertyFilter.All;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task SelectPendingAsync()
    {
        ActiveFilter = DeveloperPropertyFilter.Pending;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task SelectForSaleAsync()
    {
        ActiveFilter = DeveloperPropertyFilter.ForSale;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task SelectSoldAsync()
    {
        ActiveFilter = DeveloperPropertyFilter.Sold;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task SelectEndedAsync()
    {
        ActiveFilter = DeveloperPropertyFilter.Ended;
        return Task.CompletedTask;
    }

    // ── Add property ──

    /// <summary>
    /// The developer add-property web page. The chrome query parameters hide the site's own
    /// header, footer and back button, so the native page template is the only frame around
    /// the web view.
    /// </summary>
    private const string AddPropertyUrl =
        "https://developer-devnet.realxmarket.io/developer/add-property?headerIsVisible=false&footerIsVisible=false&backButtonIsVisible=false";

    [RelayCommand]
    private Task AddPropertyAsync() => NavigationModel.PushAsync(
        new PlutoFramework.Components.XcavateProperty.AddPropertyPage(AddPropertyUrl));

    // ── Refresh / paging ──

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

            ResetProperties();
            await LoadMorePropertiesAsync(token).ConfigureAwait(false);

            await LoadFiguresAsync(token).ConfigureAwait(false);

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

    public Task TryLoadMorePropertiesAsync(CancellationToken token) => LoadMorePropertiesAsync(token);

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

    private async Task RestartPropertiesLoadAsync(CancellationToken externalToken)
    {
        try
        {
            var token = ReplaceLoadingToken(externalToken);

            ResetProperties();
            await LoadMorePropertiesAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the user refreshes or picks another filter.
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private async Task LoadMorePropertiesAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // The developer's listings live in the selected cluster's marketplace deployment,
        // read through that cluster's indexer - the wallet there is the Solana key.
        var address = KeysModel.GetSolanaAddress();

        if (string.IsNullOrWhiteSpace(address))
        {
            ResetProperties();
            return;
        }

        if (!string.Equals(developerAddress, address, StringComparison.Ordinal))
        {
            // A different wallet inherits nothing from the previous one's list.
            developerAddress = address;
            ResetProperties();
        }

        if (!hasMore)
        {
            return;
        }

        // Where no marketplace is deployed the list stays empty rather than erroring.
        if (!XcavateDeploymentModel.IsDeployed(SolanaNetworkModel.SelectedCluster))
        {
            hasMore = false;
            MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(EmptyListMessage)));
            return;
        }

        await loadMoreSemaphore.WaitAsync(token).ConfigureAwait(false);

        try
        {
            if (PropertiesLoading || !hasMore)
            {
                return;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                PropertiesLoading = true;
            });

            var cluster = SolanaNetworkModel.SelectedCluster;

            var (_, page) = await XcavateMarketplaceIndexerModel.GetDeveloperPropertiesAsync(
                    cluster, address, ActiveFilter, first: PageSize, offset, token)
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
                    page.Select(listing => XcavatePropertyModel.ToXcavateNftWrapperAsync(listing, token)))
                .ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            var newItems = new List<XcavateNftWrapper>();

            foreach (var wrappedProperty in wrappedBatch)
            {
                if (propertiesDict.ContainsKey(wrappedProperty.Key))
                {
                    continue;
                }

                propertiesDict[wrappedProperty.Key] = wrappedProperty;
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
                        DeveloperProperties.Add(wrappedProperty);
                    }
                });
            }

            listLoadFailed = false;
        }
        catch (OperationCanceledException)
        {
            // Expected when user refreshes or leaves the page.
        }
        catch (Exception ex)
        {
            // Includes the indexer predating the developer filters (unknown args): show the
            // empty state with a retry caption, never crash (deploy order: indexer first).
            Console.WriteLine("Developer properties list error:");
            Console.WriteLine(ex);
            hasMore = false;
            listLoadFailed = true;
        }
        finally
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                PropertiesLoading = false;
                OnPropertyChanged(nameof(NoProperties));
                OnPropertyChanged(nameof(EmptyListMessage));
            });

            loadMoreSemaphore.Release();
        }
    }

    /// <summary>
    /// The two figures above the list. Each is a <c>totalCount</c> at <c>first: 1</c> on its
    /// own filter, so no page is fetched just to count, and both are shown whichever tab is
    /// active. A failure keeps the previous figure - a pull-to-refresh is the retry.
    /// </summary>
    private async Task LoadFiguresAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(developerAddress)
            || !XcavateDeploymentModel.IsDeployed(SolanaNetworkModel.SelectedCluster))
        {
            return;
        }

        var cluster = SolanaNetworkModel.SelectedCluster;

        try
        {
            var (activeListed, _) = await XcavateMarketplaceIndexerModel.GetDeveloperPropertiesAsync(
                    cluster, developerAddress, DeveloperPropertyFilter.ForSale, first: 1, offset: 0, token)
                .ConfigureAwait(false);

            MainThread.BeginInvokeOnMainThread(() => ActiveListedCount = activeListed);
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer refresh starts.
        }
        catch (Exception ex)
        {
            Console.WriteLine("Developer active listed count error:");
            Console.WriteLine(ex);
        }

        try
        {
            var sold = await XcavateMarketplaceIndexerModel.GetDeveloperSoldCountAsync(
                    cluster, developerAddress, token)
                .ConfigureAwait(false);

            MainThread.BeginInvokeOnMainThread(() => SoldCount = sold);
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer refresh starts.
        }
        catch (Exception ex)
        {
            Console.WriteLine("Developer sold count error:");
            Console.WriteLine(ex);
        }
    }

    private void ResetProperties()
    {
        offset = 0;
        hasMore = true;
        listLoadFailed = false;
        propertiesDict.Clear();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            DeveloperProperties.Clear();
            OnPropertyChanged(nameof(NoProperties));
            OnPropertyChanged(nameof(EmptyListMessage));
        });
    }

    private void OnDeveloperPropertiesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(NoProperties));
        OnPropertyChanged(nameof(ShowSkeleton));
    }

    private void OnSolanaClusterChanged(object? sender, SolanaCluster cluster)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            OnPropertyChanged(nameof(EmptyListMessage));

            _ = RefreshAsync(CancellationToken.None);
        });
    }
}
