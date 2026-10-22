using System.Reflection;
using System.Text;

namespace McDownload.Model;

public sealed record LicenseDocument(string FileName, string Title, string Content)
{
    private static readonly (string FileName, string Title)[] OrderedFiles =
    {
        ("LICENSE", "GNU General Public License v3"),
        ("HMCL-ADDITIONAL-TERMS.md", "HMCL 附加条款"),
        ("NOTICE", "来源与修改声明"),
        ("THIRD-PARTY-NOTICES.md", "CommunityToolkit.Mvvm · MIT 与第三方声明")
    };

    public static IReadOnlyList<LicenseDocument> LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(LicenseDocument).Assembly;
        return OrderedFiles.Select(file =>
        {
            string resourceName = "McDownload.Licenses." + file.FileName;
            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"内嵌许可文件不存在：{file.FileName}");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return new LicenseDocument(file.FileName, file.Title, reader.ReadToEnd());
        }).ToArray();
    }
}

public sealed record LicenseGroup(string Title, IReadOnlyList<LicenseDocument> Documents);
