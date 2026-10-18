namespace XcavateMobileApp.Components;

public partial class TermsUploadFailedPopupView : ContentView
{
    public TermsUploadFailedPopupView()
    {
        InitializeComponent();

        BindingContext = DependencyService.Get<TermsUploadFailedPopupViewModel>();
    }
}
