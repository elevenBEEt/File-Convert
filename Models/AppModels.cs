using System.IO;

namespace SDS_Convert.Models;

public sealed record RecentConversion(string InputPath, string OutputPath, DateTime CompletedAt)
{
    public string DisplayText => $"{Path.GetFileName(OutputPath)}   ·   {CompletedAt:g}";
    public override string ToString() => DisplayText;
}

public sealed record HelpArticle(string Title, string Body)
{
    public override string ToString() => Title;
}
