using PlutoFramework.Templates.PageTemplate;

namespace XcavateMobileApp.Pages;

/// <summary>
/// Pushed from the developer main page's company card. Refreshes on every appearing so an
/// edit or a new registration made on the form page above it shows up straight away.
/// </summary>
public partial class CompanyDetailPage : PageTemplate
{
    private readonly CompanyDetailViewModel viewModel = new();

    public CompanyDetailPage()
    {
        InitializeComponent();

        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        _ = viewModel.RefreshAsync();
    }
}
