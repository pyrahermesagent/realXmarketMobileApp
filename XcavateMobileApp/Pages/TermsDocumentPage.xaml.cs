using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Mvvm.Input;
using PlutoFramework.Model;
using PlutoFramework.Model.Xcavate.Profile;
using System.Linq;

#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using System.Diagnostics;
#endif

#if IOS
using Foundation;
#endif

namespace XcavateMobileApp.Pages;

/// <summary>
/// Reads the lawyer's terms PDF in-app. Android's WebView cannot render PDFs inline, so the
/// page loads a bundled pdf.js viewer from the app assets and hands it the downloaded PDF
/// bytes; WKWebView on iOS/MacCatalyst renders the PDF document directly.
/// </summary>
public partial class TermsDocumentPage : ContentPage
{
#if ANDROID
    private const string ViewerUrl = "file:///android_asset/pdfjs/termsviewer.html";

    // Upper bound for the pdf.js render + status polling in RenderPdfAsync; exceeding it
    // shows the retry view.
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(60);
#endif

    private readonly string documentUrl;
    private readonly string documentTitle;
    private readonly string walletAddress;

    public TermsDocumentPage(string title, string documentUrl, string walletAddress)
    {
        NavigationPage.SetHasNavigationBar(this, false);
        Shell.SetNavBarIsVisible(this, false);

        InitializeComponent();

        documentTitle = title;
        this.documentUrl = documentUrl;
        this.walletAddress = walletAddress;

        topBar.Title = title;
        topBar.Extra2Image = ImageSource.FromFile("xcavatetrackdownload.png");
        topBar.Extra2Command = new AsyncRelayCommand(DownloadAsync);
        topBar.Extra1Image = ImageSource.FromFile("xcavatetrash.png");
        topBar.Extra1Command = new AsyncRelayCommand(DeleteAsync);
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

#if ANDROID
        webView.Source = new UrlWebViewSource { Url = ViewerUrl };
#else
        webView.Source = new UrlWebViewSource { Url = documentUrl };
#endif
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

            return;
        }

#if ANDROID
        // The viewer shell is loaded; it can take the document now.
        _ = RenderPdfAsync();
#endif
    }

    private void OnRetryClicked(object? sender, EventArgs e)
    {
        // Reassigning the same source does not reload; clearing it first does.
        webView.Source = null;
        Load();
    }

#if ANDROID
    private async Task RenderPdfAsync()
    {
        loadingIndicator.IsVisible = true;
        loadingIndicator.IsRunning = true;

        try
        {
            using var client = new HttpClient();
            var pdf = await client.GetByteArrayAsync(documentUrl);

            // loadPdf only starts rendering and returns synchronously: evaluateJavascript
            // cannot await a Promise's value, so the outcome is polled from getPdfStatus()
            // instead — "ok" once every page is rendered, "error: ..." on failure.
            await webView.EvaluateJavaScriptAsync($"loadPdf('{Convert.ToBase64String(pdf)}')");

            var status = "pending";
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < RenderTimeout && status is "pending" or "loading")
            {
                await Task.Delay(250);

                // A null evaluation result is treated as still-pending, not as failure:
                // the loop is bounded by RenderTimeout, which shows the retry view.
                status = (await webView.EvaluateJavaScriptAsync("getPdfStatus()"))?.Trim('"') ?? "pending";
            }

            errorView.IsVisible = status != "ok";
        }
        catch
        {
            errorView.IsVisible = true;
        }
        finally
        {
            loadingIndicator.IsVisible = false;
            loadingIndicator.IsRunning = false;
        }
    }
#endif

    private async Task DownloadAsync()
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
        var request = new DownloadManager.Request(global::Android.Net.Uri.Parse(documentUrl));
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
        var bytes = await client.GetByteArrayAsync(documentUrl);

        var documentsPath = NSSearchPath.GetDirectories(NSSearchPathDirectory.DocumentDirectory, NSSearchPathDomain.User, true).FirstOrDefault()
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var fileName = SanitizeFileName(documentTitle) + ".pdf";
        var filePath = Path.Combine(documentsPath, fileName);

        await File.WriteAllBytesAsync(filePath, bytes);

        await Toast.Make($"Downloaded {fileName}").Show();
    }
#endif

    private async Task DeleteAsync()
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
