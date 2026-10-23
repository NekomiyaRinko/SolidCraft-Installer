using McDownload.i18n;
using System.Reflection;
using System.Text;

namespace McDownload.Model;

public sealed record LicenseDocument(string FileName, string Title, string Content)
{
    private static readonly (string FileName, string Title)[] OrderedFiles =
    {
        ("LICENSE", Text.Get("LicenseDocument", "GplTitle")),
        ("HMCL-ADDITIONAL-TERMS.md", Text.Get("LicenseDocument", "HmclTerms")),
        ("NOTICE", Text.Get("LicenseDocument", "SourceNotice")),
        ("THIRD-PARTY-NOTICES.md", Text.Get("LicenseDocument", "ThirdPartyNotices"))
    };

    public static IReadOnlyList<LicenseDocument> LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(LicenseDocument).Assembly;
        return OrderedFiles.Select(file =>
        {
            string resourceName = "McDownload.Licenses." + file.FileName;
            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(Text.Format("LicenseDocument", "EmbeddedLicenseMissing", file.FileName));
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return new LicenseDocument(file.FileName, file.Title, reader.ReadToEnd());
        }).ToArray();
    }
}

public sealed record LicenseGroup(string Title, IReadOnlyList<LicenseDocument> Documents);
