using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlutoFramework.Components.Buttons;
using PlutoFramework.Components.Loading;
using PlutoFramework.Model;
using PlutoFramework.Model.Xcavate.Profile;
using Company = XcavateProfile.Client.Company;

namespace XcavateMobileApp.Pages;

/// <summary>
/// The company create/edit form. Without an existing record it registers a new company (and
/// selects it straight away); with one it replaces the stored fields. A logo picked in the
/// form uploads only after the record itself is saved - the upload endpoint answers 404 for
/// a company that does not exist yet.
/// </summary>
public partial class CompanyFormViewModel : ObservableObject
{
    private readonly Company? existing;
    private readonly XcavateCompanyService companyService = new();

    private static DeveloperMainPageViewModel MainViewModel => DependencyService.Get<DeveloperMainPageViewModel>();

    public CompanyFormViewModel(Company? existing)
    {
        this.existing = existing;

        title = existing is null ? "New company" : "Edit company";
        companyName = existing?.Name;
        email = existing?.Email;
        website = existing?.Website;
        address = existing?.Address;
        summary = existing?.Summary;
    }

    [ObservableProperty]
    private string title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveButtonState))]
    private string? companyName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveButtonState))]
    private string? email;

    [ObservableProperty]
    private string? website;

    [ObservableProperty]
    private string? address;

    [ObservableProperty]
    private string? summary;

    [ObservableProperty]
    private Stream? logoStream;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveButtonState))]
    private bool saving;

    /// <summary>Name and email are the only fields the server requires; the rest is optional.</summary>
    public ButtonStateEnum SaveButtonState =>
        !Saving && !string.IsNullOrWhiteSpace(CompanyName) && Email is not null && FormModel.IsValidEmail(Email)
            ? ButtonStateEnum.Enabled
            : ButtonStateEnum.Disabled;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SaveButtonState != ButtonStateEnum.Enabled)
        {
            return;
        }

        var loadingViewModel = DependencyService.Get<FullPageLoadingViewModel>();
        loadingViewModel.IsVisible = true;
        Saving = true;

        try
        {
            if (existing is null)
            {
                loadingViewModel.Message = "Registering company";

                var created = await companyService.CreateAsync(
                    CompanyName!.Trim(), Email!.Trim(), Website, Summary, Address, LogoStream, CancellationToken.None);

                if (created is null)
                {
                    // Nothing to sign with - no key, or a dismissed password prompt.
                    return;
                }

                // A fresh registration becomes the selected company straight away - it is
                // the one the developer just described, and usually their only one.
                MainViewModel.SelectCompany(created);
            }
            else
            {
                loadingViewModel.Message = "Updating company";

                var updated = await companyService.UpdateAsync(
                    existing, CompanyName!.Trim(), Email!.Trim(), Website, Summary, Address, LogoStream, CancellationToken.None);

                if (updated is null)
                {
                    return;
                }

                MainViewModel.SelectCompany(updated);
            }

            await NavigationModel.PopAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Company save error:");
            Console.WriteLine(ex);

            await Toast.Make($"Could not save the company: {ex.Message}").Show();
        }
        finally
        {
            Saving = false;
            loadingViewModel.IsVisible = false;
        }
    }

    [RelayCommand]
    private Task CancelAsync() => NavigationModel.PopAsync();
}
