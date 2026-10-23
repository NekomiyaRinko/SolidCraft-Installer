using McDownload.i18n;
using CommunityToolkit.Mvvm.ComponentModel;
using McDownload.Model;

namespace McDownload.ViewModel;

public partial class LicenseViewModel : ObservableObject
{
    public IReadOnlyList<LicenseDocument> Documents { get; } = LicenseDocument.LoadEmbedded();
    public IReadOnlyList<LicenseGroup> Groups { get; }
    public LicenseDocument ProjectLicense => Documents[0];
    [ObservableProperty] private LicenseDocument selectedDocument;

    public LicenseViewModel()
    {
        selectedDocument = ProjectLicense;
        Groups = new[]
        {
            new LicenseGroup(Text.Get("License", "HmclGroup"), Documents.Take(3).ToArray()),
            new LicenseGroup(Text.Get("License", "CommunityToolkitGroup"), Documents.Skip(3).ToArray())
        };
    }
}
