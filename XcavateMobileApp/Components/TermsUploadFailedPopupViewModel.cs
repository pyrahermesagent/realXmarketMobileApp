using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlutoFramework.Model;

namespace XcavateMobileApp.Components;

/// <summary>
/// Bottom-card popup for lawyer terms-document upload failures. The title is fixed
/// ("Upload failed"); the page's view model sets <see cref="Message"/> and flips
/// <see cref="IsVisible"/>. Drag-to-dismiss and the OK button both land in
/// <see cref="SetToDefault"/> (the former via BottomPopupCard's IPopup write-back).
/// </summary>
public partial class TermsUploadFailedPopupViewModel : ObservableObject, IPopup, ISetToDefault
{
    [ObservableProperty]
    private bool isVisible = false;

    [ObservableProperty]
    private string message = "";

    [RelayCommand]
    private void Ok() => SetToDefault();

    public void SetToDefault()
    {
        IsVisible = false;
        Message = "";
    }
}
