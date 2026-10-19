using PlutoFramework.Templates.PageTemplate;
using Company = XcavateProfile.Client.Company;

namespace XcavateMobileApp.Pages;

/// <summary>
/// Pushed from the developer main page's company card (create) or the company detail page
/// (edit). The constructor argument decides which: null registers a new company.
/// </summary>
public partial class CompanyFormPage : PageTemplate
{
    public CompanyFormPage(Company? existing = null)
    {
        InitializeComponent();

        BindingContext = new CompanyFormViewModel(existing);
    }
}
