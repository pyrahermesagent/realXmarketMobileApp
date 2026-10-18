using CommunityToolkit.Maui.Alerts;
using PlutoFramework.Model;
using PlutoFramework.Model.Xcavate.Profile;
using System.Linq;

#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
#endif

#if IOS
using Foundation;
#endif

namespace XcavateMobileApp.Pages;

/// <summary>
/// Reads the lawyer's terms PDF in-app. The document lives on a public IPFS gateway;
/// Android's WebView cannot render PDFs inline, so there the page goes through the
/// Google Docs embedded viewer (which needs the gateway URL to be publicly reachable),
/// while WKWebView on iOS/MacCatalyst renders the PDF directly.
/// </summary>
public partial class TermsDocumentPage : ContentPage
{
    private readonly string sourceUrl;
    private readonly string gatewayUrl;
    private readonly string documentTitle;
    private readonly string walletAddress;

    public TermsDocumentPage(string title, string gatewayUrl, string walletAddress)
    {
        NavigationPage.SetHasNavigationBar(this, false);
        Shell.SetNavBarIsVisible(this, false);

        InitializeComponent();

        documentTitle = title;
        this.gatewayUrl = gatewayUrl;
        this.walletAddress = walletAddress;
        titleSpan.Text = title;

#if ANDROID
        sourceUrl = "https://docs.google.com/viewer?embedded=true&url=" + Uri.EscapeDataString(gatewayUrl);
#else
        sourceUrl = gatewayUrl;
#endif
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (webView.Source is null)
        {
            Load();
        }
    }

    private void Load()
    {
        errorView.IsVisible = false;
        loadingIndicator.IsVisible = true;
        loadingIndicator.IsRunning = true;

        webView.Source = new UrlWebViewSource { Url = sourceUrl };
    }

    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        errorView.IsVisible = false;
        loadingIndicator.IsVisible = true;
        loadingIndicator.IsRunning = true;
    }

    private void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        loadingIndicator.IsVisible = false;
        loadingIndicator.IsRunning = false;

        if (e.Result != WebNavigationResult.Success)
        {
            errorView.IsVisible = true;
        }
    }

    private void OnRetryClicked(object? sender, EventArgs e)
    {
        // Reassigning the same source does not reload; clearing it first does.
        webView.Source = null;
        Load();
    }

    private async void OnBackClicked(object? sender, TappedEventArgs e)
    {
        await NavigationModel.PopAsync();
    }

    private async void OnDownloadClicked(object? sender, TappedEventArgs e)
    {
        try
        {
            loadingIndicator.IsVisible = true;
            loadingIndicator.IsRunning = true;

#if ANDROID
            await DownloadAndroidAsync();
#else
            await DownloadIosAsync();
#endif
        }
        catch (Exception ex)
        {
            await Toast.Make($"Could not download document: {ex.Message}").Show();
        }
        finally
        {
            loadingIndicator.IsVisible = false;
            loadingIndicator.IsRunning = false;
        }
    }

#if ANDROID
    private Task DownloadAndroidAsync()
    {
        var context = Android.App.Application.Context;
        var downloadManager = (DownloadManager?)context.GetSystemService(Context.DownloadService);

        if (downloadManager is null)
        {
            return Toast.Make("Download manager is not available.").Show();
        }

        var fileName = SanitizeFileName(documentTitle) + ".pdf";
        var request = new DownloadManager.Request(global::Android.Net.Uri.Parse(gatewayUrl));
        request.SetTitle(documentTitle);
        request.SetDescription("PDF document");
        request.SetNotificationVisibility(DownloadVisibility.VisibleNotifyCompleted);
        request.SetDestinationInExternalPublicDir(Android.OS.Environment.DirectoryDownloads, fileName);

        downloadManager.Enqueue(request);

        return Toast.Make("Download started.").Show();
    }
#endif

#if IOS
    private async Task DownloadIosAsync()
    {
        using var client = new HttpClient();
        var bytes = await client.GetByteArrayAsync(gatewayUrl);

        var documentsPath = NSSearchPath.GetDirectories(NSSearchPathDirectory.DocumentDirectory, NSSearchPathDomain.User, true).FirstOrDefault()
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var fileName = SanitizeFileName(documentTitle) + ".pdf";
        var filePath = Path.Combine(documentsPath, fileName);

        await File.WriteAllBytesAsync(filePath, bytes);

        await Toast.Make($"Downloaded {fileName}").Show();
    }
#endif

    private async void OnDeleteClicked(object? sender, TappedEventArgs e)
    {
        var confirmed = await DisplayAlert(
            "Delete document",
            "This will permanently delete the terms document from the server. Are you sure?",
            "Delete",
            "Cancel");

        if (!confirmed)
        {
            return;
        }

        try
        {
            loadingIndicator.IsVisible = true;
            loadingIndicator.IsRunning = true;

            await TermsDocumentClient.DeleteAsync(walletAddress, CancellationToken.None);

            await Toast.Make("Document deleted.").Show();
            await NavigationModel.PopAsync();
        }
        catch (Exception ex)
        {
            await Toast.Make($"Could not delete document: {ex.Message}").Show();
        }
        finally
        {
            loadingIndicator.IsVisible = false;
            loadingIndicator.IsRunning = false;
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "document" : name.Trim();
    }
}
