using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlutoFramework;
using PlutoFramework.Components.XcavateProperty;
using PlutoFramework.Model;
using PlutoFramework.Model.Xcavate;
using PlutoFramework.Model.Xcavate.Profile;
using PlutoFrameworkCore.Solana;
using PlutoFrameworkCore.Xcavate;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using XcavateMobileApp.Components;
using NftKey = (UniqueryPlus.NftTypeEnum, System.Numerics.BigInteger, System.Numerics.BigInteger);
using XcavatePropertyModel = PlutoFramework.Components.XcavateProperty.XcavatePropertyModel;

namespace XcavateMobileApp.Pages;

/// <summary>
/// The lawyer landing page: a "Manage properties" count cell, the terms-document card
/// (upload/view the wallet's terms PDF through the profile API) and a property list whose
/// two filters - "Newly listed" (claimable sold-out listings) and "Under Instruction"
/// (the wallet already sits in a lawyer slot) - both page through the indexer's
/// <c>LawyerProperties</c> query.
/// </summary>
/// <remarks>
/// Singleton, like <see cref="InvestorMainPageViewModel"/> - registered in App.xaml.cs and
/// never unsubscribed from the static events it listens to. Failures from the indexer or
/// the profile API land in the empty/error states, never in a crash: the deployed indexer
/// may lag the app release (the <c>assignedLawyer</c>/<c>assignableTo</c> args would be
/// unknown server-side), which must look like an empty page with a retry, not an
/// exception.
/// </remarks>
public partial class LawyerMainPageViewModel : ObservableObject
{
    private const int PageSize = 20;

    /// <summary>The card's own cap; the API accepts 25 MB of PDF plus multipart overhead.</summary>
    private const long MaxTermsPdfBytes = 25L * 1024 * 1024;

    private static readonly FilePickerFileType PdfFileTypes = new(
        new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            { DevicePlatform.iOS, new[] { "com.adobe.pdf" } }, // UTType values
            { DevicePlatform.Android, new[] { "application/pdf" } }, // MIME type
            { DevicePlatform.WinUI, new[] { ".pdf" } }, // file extension
            { DevicePlatform.macOS, new[] { "com.adobe.pdf" } }, // UTType values
            { DevicePlatform.Tizen, new[] { "*/*" } },
        });

    private readonly Dictionary<NftKey, XcavateNftWrapper> propertiesDict = [];
    private readonly object loadingLock = new();
    private readonly SemaphoreSlim loadMoreSemaphore = new(1, 1);
    private CancellationTokenSource? loadingCts;
    private string lawyerAddress = string.Empty;
    private int offset;
    private bool hasMore = true;
    private bool listLoadFailed;

    public LawyerMainPageViewModel()
    {
        LawyerProperties.CollectionChanged += OnLawyerPropertiesCollectionChanged;

        // A network switch re-reads the list from the newly selected cluster's indexer.
        // Singleton lifetime: never unsubscribed, same convention as the investor page.
        SolanaNetworkModel.ClusterChanged += OnSolanaClusterChanged;
    }

    // ── List state ──

    [ObservableProperty]
    private bool isRefreshing;

    private bool isRefreshInProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoProperties))]
    private ObservableCollection<XcavateNftWrapper> lawyerProperties = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoProperties))]
    [NotifyPropertyChangedFor(nameof(ShowSkeleton))]
    private bool propertiesLoading;

    public bool NoProperties => !PropertiesLoading && LawyerProperties.Count == 0;

    /// <summary>True while the list is still empty and loading (initial load / refresh).</summary>
    public bool ShowSkeleton => PropertiesLoading && LawyerProperties.Count == 0;

    /// <summary>
    /// The empty-list caption: a cluster with no marketplace deployment explains itself,
    /// a failed load offers a retry, otherwise each filter names its own empty state.
    /// </summary>
    public string EmptyListMessage =>
        !XcavateDeploymentModel.IsDeployed(SolanaNetworkModel.SelectedCluster)
            ? XcavateDeploymentModel.NotDeployedMessage(SolanaNetworkModel.SelectedCluster)
            : listLoadFailed
                ? "The properties could not be loaded. Pull down to try again."
                : UnderInstructionActive
                    ? "Properties you are instructed on will appear here."
                    : "Newly listed properties awaiting a lawyer will appear here.";

    // ── Stats ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignedCountText))]
    private int assignedCount;

    public string AssignedCountText => AssignedCount.ToString();

    // ── Filter toggles ──

    [ObservableProperty]
    private bool newlyListedActive = true;

    [ObservableProperty]
    private bool underInstructionActive = false;

    [ObservableProperty]
    private PlutoFramework.Components.Buttons.ButtonStateEnum newlyListedButtonState =
        PlutoFramework.Components.Buttons.ButtonStateEnum.Enabled;

    [ObservableProperty]
    private PlutoFramework.Components.Buttons.ButtonStateEnum underInstructionButtonState =
        PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;

    /// <summary>
    /// The two filters are mutually exclusive and one is always on - the indexer query
    /// needs one of its two lawyer arguments, so unlike the investor page's toggles the
    /// active one cannot be tapped off.
    /// </summary>
    partial void OnNewlyListedActiveChanged(bool value)
    {
        NewlyListedButtonState = value
            ? PlutoFramework.Components.Buttons.ButtonStateEnum.Enabled
            : PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;

        if (value)
        {
            UnderInstructionActive = false;
            _ = RestartPropertiesLoadAsync(CancellationToken.None);
        }
    }

    partial void OnUnderInstructionActiveChanged(bool value)
    {
        UnderInstructionButtonState = value
            ? PlutoFramework.Components.Buttons.ButtonStateEnum.Enabled
            : PlutoFramework.Components.Buttons.ButtonStateEnum.GrayEnabled;

        if (value)
        {
            NewlyListedActive = false;
            _ = RestartPropertiesLoadAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    private void ToggleNewlyListed()
    {
        if (!NewlyListedActive)
        {
            NewlyListedActive = true;
        }
    }

    [RelayCommand]
    private void ToggleUnderInstruction()
    {
        if (!UnderInstructionActive)
        {
            UnderInstructionActive = true;
        }
    }

    /// <summary>The "Manage properties" cell's tap: jump straight to the instructed list.</summary>
    [RelayCommand]
    private Task ShowUnderInstructionAsync()
    {
        if (!UnderInstructionActive)
        {
            UnderInstructionActive = true;
        }

        return Task.CompletedTask;
    }

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

            await LoadTermsDocumentAsync(token).ConfigureAwait(false);

            ResetProperties();
            await LoadMorePropertiesAsync(token).ConfigureAwait(false);

            await LoadAssignedCountAsync(token).ConfigureAwait(false);

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
            // Expected when the user refreshes or flips the filter again.
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private async Task LoadMorePropertiesAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // The lawyer's listings live in the selected cluster's marketplace deployment,
        // read through that cluster's indexer - the wallet there is the Solana key.
        var address = KeysModel.GetSolanaAddress();

        if (string.IsNullOrWhiteSpace(address))
        {
            ResetProperties();
            return;
        }

        if (!string.Equals(lawyerAddress, address, StringComparison.Ordinal))
        {
            // A different wallet inherits nothing from the previous one's list.
            lawyerAddress = address;
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

            var (totalCount, page) = UnderInstructionActive
                ? await XcavateMarketplaceIndexerModel.GetLawyerAssignedPropertiesAsync(
                        cluster, address, first: PageSize, offset, token)
                    .ConfigureAwait(false)
                : await XcavateMarketplaceIndexerModel.GetLawyerAssignablePropertiesAsync(
                        cluster, address, first: PageSize, offset, token)
                    .ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            // The count cell answers from the same query while the assigned filter is on;
            // on the assignable filter it is fetched separately (RefreshAsync).
            if (UnderInstructionActive)
            {
                MainThread.BeginInvokeOnMainThread(() => AssignedCount = totalCount);
            }

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
                        LawyerProperties.Add(wrappedProperty);
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
            // Includes the indexer predating the lawyer filters (unknown args): show the
            // empty state with a retry caption, never crash (deploy order: indexer first).
            Console.WriteLine("Lawyer properties list error:");
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
    /// The "Manage properties" figure: the assigned query's totalCount at first:1, so no
    /// full page is fetched just to count. A failure keeps the previous figure.
    /// </summary>
    private async Task LoadAssignedCountAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(lawyerAddress)
            || !XcavateDeploymentModel.IsDeployed(SolanaNetworkModel.SelectedCluster))
        {
            return;
        }

        try
        {
            var (totalCount, _) = await XcavateMarketplaceIndexerModel.GetLawyerAssignedPropertiesAsync(
                    SolanaNetworkModel.SelectedCluster, lawyerAddress, first: 1, offset: 0, token)
                .ConfigureAwait(false);

            MainThread.BeginInvokeOnMainThread(() => AssignedCount = totalCount);
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer refresh starts.
        }
        catch (Exception ex)
        {
            Console.WriteLine("Lawyer assigned count error:");
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
            LawyerProperties.Clear();
            OnPropertyChanged(nameof(NoProperties));
            OnPropertyChanged(nameof(EmptyListMessage));
        });
    }

    private void OnLawyerPropertiesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
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

    // ── Terms document card ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTermsUpload))]
    [NotifyPropertyChangedFor(nameof(ShowTermsUploaded))]
    [NotifyPropertyChangedFor(nameof(TermsFileNameText))]
    [NotifyPropertyChangedFor(nameof(TermsUpdatedAtText))]
    private TermsDocumentInfo? termsDocument;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTermsUpload))]
    [NotifyPropertyChangedFor(nameof(ShowTermsUploaded))]
    [NotifyPropertyChangedFor(nameof(ShowTermsBusy))]
    private bool termsBusy;

    public bool ShowTermsBusy => TermsBusy;

    /// <summary>No document on record: offer the upload button.</summary>
    public bool ShowTermsUpload => !TermsBusy && TermsDocument is null;

    /// <summary>A document is on record: its name, date and the View/Change actions.</summary>
    public bool ShowTermsUploaded => !TermsBusy && TermsDocument is not null;

    public string TermsFileNameText => TermsDocument?.FileName ?? string.Empty;

    public string TermsUpdatedAtText => TermsDocument is null
        ? string.Empty
        : $"Updated {TermsDocument.UpdatedAt.LocalDateTime:d MMM yyyy}";

    /// <summary>
    /// The card's refresh: GET the wallet's terms document. 404 (none yet) is the normal
    /// empty state; any other failure is logged and leaves the previous document (if any)
    /// on the card - a pull-to-refresh is the retry.
    /// </summary>
    private async Task LoadTermsDocumentAsync(CancellationToken token)
    {
        var address = KeysModel.GetSolanaAddress();

        if (string.IsNullOrWhiteSpace(address))
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                TermsDocument = null;
            });
            return;
        }

        try
        {
            var info = await TermsDocumentClient.GetAsync(address, token).ConfigureAwait(false);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                TermsDocument = info;
            });
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer refresh starts.
        }
        catch (Exception ex)
        {
            Console.WriteLine("Terms document load error:");
            Console.WriteLine(ex);
        }
    }

    /// <summary>
    /// Picks a PDF (25 MB cap), uploads it signed, and swaps the card to the uploaded
    /// state. Every failure restores the previous card state and explains itself through
    /// the terms-upload-failed bottom popup - a rejected signing prompt included.
    /// </summary>
    [RelayCommand]
    private async Task UploadTermsAsync()
    {
        if (TermsBusy)
        {
            return;
        }

        var address = KeysModel.GetSolanaAddress();

        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        FileResult? picked;

        try
        {
            picked = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select your terms PDF",
                FileTypes = PdfFileTypes,
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("Terms PDF pick error:");
            Console.WriteLine(ex);
            ShowUploadError("The file could not be opened. Try again.");
            return;
        }

        if (picked is null)
        {
            return;
        }

        if (!string.Equals(Path.GetExtension(picked.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            ShowUploadError("Only PDF files can be uploaded.");
            return;
        }

        MemoryStream pdf;

        try
        {
            await using var pickedStream = await picked.OpenReadAsync();
            pdf = await ReadCappedAsync(pickedStream, MaxTermsPdfBytes, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            ShowUploadError("The PDF is too large - the limit is 25 MB.");
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Terms PDF read error:");
            Console.WriteLine(ex);
            ShowUploadError("The file could not be read. Try again.");
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            TermsBusy = true;
        });

        try
        {
            pdf.Position = 0;

            var info = await TermsDocumentClient.UploadAsync(
                address, picked.FileName, pdf, CancellationToken.None).ConfigureAwait(false);

            MainThread.BeginInvokeOnMainThread(() => TermsDocument = info);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Terms document upload error:");
            Console.WriteLine(ex);

            // TermsDocument was never cleared, so the card falls back to the previous
            // document (or the upload offer) on its own once the busy flag drops.
            MainThread.BeginInvokeOnMainThread(() =>
                ShowUploadError(ex.Message));
        }
        finally
        {
            MainThread.BeginInvokeOnMainThread(() => TermsBusy = false);
        }
    }

    [RelayCommand]
    private async Task ViewTermsAsync()
    {
        if (TermsDocument is null)
        {
            return;
        }

        await NavigationModel.PushAsync(new TermsDocumentPage("Terms document", TermsDocument.Url));
    }

    /// <summary>
    /// Reads the picked file into memory, refusing to grow past <paramref name="maxBytes"/>:
    /// <see cref="FileResult"/> carries no length, so the cap is enforced while copying.
    /// </summary>
    private static async Task<MemoryStream> ReadCappedAsync(Stream source, long maxBytes, CancellationToken token)
    {
        var memory = new MemoryStream();
        var buffer = new byte[81920];
        var total = 0L;

        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);

            if (read == 0)
            {
                return memory;
            }

            total += read;

            if (total > maxBytes)
            {
                memory.Dispose();
                throw new InvalidDataException("The file exceeds the size limit.");
            }

            memory.Write(buffer, 0, read);
        }
    }

    private static void ShowUploadError(string text)
    {
        var popup = DependencyService.Get<TermsUploadFailedPopupViewModel>();

        popup.Message = text;
        popup.IsVisible = true;
    }
}
