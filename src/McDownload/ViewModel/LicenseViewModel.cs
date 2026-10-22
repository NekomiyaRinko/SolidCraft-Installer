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
            new LicenseGroup("SolidCraft Installer · HMCL 改编部分", Documents.Take(3).ToArray()),
            new LicenseGroup("CommunityToolkit.Mvvm 8.4.0", Documents.Skip(3).ToArray())
        };
    }
}
