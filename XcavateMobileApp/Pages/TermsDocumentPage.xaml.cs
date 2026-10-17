using PlutoFramework.Model;

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

    public TermsDocumentPage(string title, string gatewayUrl)
    {
        NavigationPage.SetHasNavigationBar(this, false);
        Shell.SetNavBarIsVisible(this, false);

        InitializeComponent();

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
}
