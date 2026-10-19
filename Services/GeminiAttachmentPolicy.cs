namespace OMMS.Services;

public static class GeminiAttachmentPolicy
{
    public const long MaxFileSize = 10 * 1024 * 1024;

    private static readonly Dictionary<string, string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".txt"] = "text/plain",
            [".csv"] = "text/csv",
            [".md"] = "text/markdown"
        };

    public static string? Validate(string fileName, long size)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "ファイル名を確認できません。";
        if (!AllowedExtensions.ContainsKey(Path.GetExtension(fileName)))
            return "PDF、PNG、JPEG、TXT、CSV、Markdownのみ添付できます。";
        if (size <= 0) return "空のファイルは添付できません。";
        if (size > MaxFileSize) return "添付ファイルは10MB以内にしてください。";
        return null;
    }

    public static string GetContentType(string fileName) =>
        AllowedExtensions.TryGetValue(Path.GetExtension(fileName), out var contentType)
            ? contentType
            : throw new ArgumentException("未対応のファイル形式です。", nameof(fileName));
}
