namespace OMMS.Services;

public static class ChatInputPolicy
{
    public const int MaxLength = 10_000;

    public static string? Validate(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "質問を入力してください。";

        if (message.Length > MaxLength)
            return $"質問は{MaxLength:N0}文字以内で入力してください。";

        return null;
    }
}
