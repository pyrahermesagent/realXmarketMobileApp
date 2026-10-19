using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlutoFramework.Model;
using PlutoFramework.Model.Xcavate.Profile;
using System.Collections.ObjectModel;
using Company = XcavateProfile.Client.Company;

namespace XcavateMobileApp.Pages;

/// <summary>
/// The selected company's detail page: its stored fields on top, and - when the wallet owns
/// more than one - the full list underneath to switch the selection with a tap, plus the way
/// to register another one. Edit opens the form page; delete asks first, then removes the
/// record from the server.
/// </summary>
/// <remarks>
/// Reads through <see cref="DeveloperMainPageViewModel"/> (the singleton the card binds to)
/// rather than holding its own copy of the list, so a selection made here and the name on
/// the main page's card can never disagree.
/// </remarks>
public partial class CompanyDetailViewModel : ObservableObject
{
    private readonly XcavateCompanyService companyService = new();

    private static DeveloperMainPageViewModel MainViewModel => DependencyService.Get<DeveloperMainPageViewModel>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(HasLogo))]
    [NotifyPropertyChangedFor(nameof(LogoSource))]
    [NotifyPropertyChangedFor(nameof(CompanyName))]
    [NotifyPropertyChangedFor(nameof(EmailText))]
    [NotifyPropertyChangedFor(nameof(WebsiteText))]
    [NotifyPropertyChangedFor(nameof(HasWebsite))]
    [NotifyPropertyChangedFor(nameof(AddressText))]
    [NotifyPropertyChangedFor(nameof(HasAddress))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private Company? company;

    public ObservableCollection<CompanyListItemViewModel> Companies { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    private bool busy;

    public bool ShowContent => !Busy;

    public string Title => Company?.Name ?? "Company";

    public bool HasLogo => Company?.Logo is not null;

    public ImageSource? LogoSource => Company?.Logo is string logoUrl
        ? ImageSource.FromUri(new Uri(logoUrl))
        : null;

    public string CompanyName => Company?.Name ?? string.Empty;

    public string EmailText => Company?.Email ?? string.Empty;

    public string WebsiteText => Company?.Website ?? string.Empty;

    public bool HasWebsite => !string.IsNullOrWhiteSpace(Company?.Website);

    public string AddressText => Company?.Address ?? string.Empty;

    public bool HasAddress => !string.IsNullOrWhiteSpace(Company?.Address);

    public string SummaryText => Company?.Summary ?? string.Empty;

    public bool HasSummary => !string.IsNullOrWhiteSpace(Company?.Summary);

    /// <summary>The switch-selection list only earns its space from the second company on.</summary>
    public bool HasMultipleCompanies => Companies.Count > 1;

    /// <summary>
    /// Re-fetches the wallet's companies (through the main page's view model) and re-reads
    /// the selection. Runs on every appearing, so an edit made on the form page shows up
    /// without any manual refresh.
    /// </summary>
    public async Task RefreshAsync()
    {
        Busy = true;

        try
        {
            await MainViewModel.ReloadCompaniesAsync();
        }
        finally
        {
            Busy = false;
        }

        SyncFromMainViewModel();
    }

    private void SyncFromMainViewModel()
    {
        Company = MainViewModel.SelectedCompany;

        Companies.Clear();

        foreach (var owned in MainViewModel.Companies)
        {
            Companies.Add(new CompanyListItemViewModel(owned, owned.CompanyId == Company?.CompanyId));
        }

        OnPropertyChanged(nameof(HasMultipleCompanies));
    }

    [RelayCommand]
    private async Task EditAsync()
    {
        if (Company is null)
        {
            return;
        }

        await NavigationModel.PushAsync(new CompanyFormPage(Company));
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Company?.CompanyId is not string companyId)
        {
            return;
        }

        var confirmed = await Shell.Current.DisplayAlert(
            "Delete company",
            $"This will permanently delete {Company.Name} from the server. Are you sure?",
            "Delete",
            "Cancel");

        if (!confirmed)
        {
            return;
        }

        try
        {
            var deleted = await companyService.DeleteAsync(companyId, CancellationToken.None);

            if (!deleted)
            {
                // Nothing to sign with - no key, or a dismissed password prompt.
                return;
            }

            await Toast.Make("Company deleted.").Show();

            // The reload resolves the selection onto one of the remaining companies (or the
            // register offer when none are left) before this page closes.
            await MainViewModel.ReloadCompaniesAsync();
            await NavigationModel.PopAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Company delete error:");
            Console.WriteLine(ex);

            await Toast.Make($"Could not delete company: {ex.Message}").Show();
        }
    }

    [RelayCommand]
    private void SelectCompany(CompanyListItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        MainViewModel.SelectCompany(item.Company);
        SyncFromMainViewModel();
    }

    [RelayCommand]
    private Task AddCompanyAsync() => NavigationModel.PushAsync(new CompanyFormPage());
}

/// <summary>One row of the "Your companies" switch list on <see cref="CompanyDetailPage"/>.</summary>
public partial class CompanyListItemViewModel : ObservableObject
{
    public CompanyListItemViewModel(Company company, bool isSelected)
    {
        Company = company;
        IsSelected = isSelected;
    }

    public Company Company { get; }

    [ObservableProperty]
    private bool isSelected;

    public string Name => Company.Name;

    public string EmailText => Company.Email;
}
