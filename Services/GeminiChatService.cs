using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMMS.Models;

namespace OMMS.Services;

public sealed class GeminiChatService(HttpClient httpClient, IConfiguration configuration)
{
    private const string ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";
    private const string SystemPrompt =
        "あなたは企業内業務を支援するアシスタントです。日本語で正確かつ簡潔に回答してください。";

    private readonly string _apiKey = configuration["Gemini:ApiKey"] ?? string.Empty;
    private readonly string _model = configuration["Gemini:Model"] ?? "gemini-3.6-flash";
    private readonly int _maxAttempts = ReadIntSetting(
        configuration["Gemini:RetryMaxAttempts"], defaultValue: 3, minimum: 1, maximum: 5);
    private readonly int _baseRetryDelayMilliseconds = ReadIntSetting(
        configuration["Gemini:RetryBaseDelayMilliseconds"], defaultValue: 500, minimum: 0, maximum: 5_000);

    public async Task<GeminiChatResult> SendAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
        => await SendAsync([new GeminiChatTurn("user", userMessage)], cancellationToken);

    public async Task<GeminiChatResult> SendAsync(
        IReadOnlyList<GeminiChatTurn> turns,
        CancellationToken cancellationToken = default)
    {
        if (turns.Count == 0 || turns[^1].Role != "user")
            return GeminiChatResult.Failure("質問を入力してください。");

        if (ChatInputPolicy.Validate(turns[^1].Text) is { } validationError)
            return GeminiChatResult.Failure(validationError);

        if (turns.Any(turn => turn.Role is not ("user" or "model") ||
                              string.IsNullOrWhiteSpace(turn.Text)))
            return GeminiChatResult.Failure("会話履歴の形式が正しくありません。");

        foreach (var attachment in turns.Select(turn => turn.Attachment).OfType<GeminiAttachment>())
        {
            if (GeminiAttachmentPolicy.Validate(attachment.FileName, attachment.Data.LongLength) is { } error)
                return GeminiChatResult.Failure(error);
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return GeminiChatResult.Failure(
                "Gemini APIキーが設定されていません。開発環境のシークレットに Gemini:ApiKey を設定してください。");
        }

        var requestBody = new
        {
            systemInstruction = new { parts = new[] { new { text = SystemPrompt } } },
            contents = turns.Select(turn => new
            {
                role = turn.Role,
                parts = BuildParts(turn)
            }).ToArray()
        };

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            using var request = CreateRequest(requestBody);

            try
            {
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (IsTransient(response.StatusCode) && attempt < _maxAttempts)
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return GeminiChatResult.Failure(GetHttpErrorMessage(response.StatusCode));

                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                var root = document.RootElement;
                var answer = "";

                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("candidates", out var candidates) &&
                    candidates.ValueKind == JsonValueKind.Array &&
                    candidates.GetArrayLength() > 0 &&
                    candidates[0].ValueKind == JsonValueKind.Object &&
                    candidates[0].TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.Object &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    answer = string.Join(Environment.NewLine,
                        parts.EnumerateArray()
                            .Where(part => part.ValueKind == JsonValueKind.Object &&
                                           (!part.TryGetProperty("thought", out var thought) ||
                                            thought.ValueKind != JsonValueKind.True) &&
                                           part.TryGetProperty("text", out var text) &&
                                           text.ValueKind == JsonValueKind.String)
                            .Select(part => part.GetProperty("text").GetString())
                            .Where(text => !string.IsNullOrWhiteSpace(text)));
                }

                if (string.IsNullOrWhiteSpace(answer))
                    return GeminiChatResult.Failure("Geminiから回答本文を取得できませんでした。");

                int? inputTokens = null;
                int? outputTokens = null;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("usageMetadata", out var usage) &&
                    usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("promptTokenCount", out var input) &&
                        input.ValueKind == JsonValueKind.Number && input.TryGetInt32(out var count))
                        inputTokens = count;
                    if (usage.TryGetProperty("candidatesTokenCount", out var output) &&
                        output.ValueKind == JsonValueKind.Number && output.TryGetInt32(out count))
                        outputTokens = count;
                }

                return new GeminiChatResult(true, answer, null, _model, inputTokens, outputTokens);
            }
            catch (HttpRequestException) when (attempt < _maxAttempts)
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && attempt < _maxAttempts)
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return GeminiChatResult.Failure(
                    "Gemini APIへ接続できませんでした。自動再試行でも復旧しないため、ネットワーク接続を確認してください。");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return GeminiChatResult.Failure(
                    "Gemini APIの応答がタイムアウトしました。自動再試行でも復旧しないため、少し待ってから再実行してください。");
            }
            catch (JsonException)
            {
                return GeminiChatResult.Failure("Gemini APIの応答を読み取れませんでした。");
            }
        }

        return GeminiChatResult.Failure("Gemini APIの呼び出しに失敗しました。");
    }

    private HttpRequestMessage CreateRequest(object requestBody)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ApiBaseUrl}/{Uri.EscapeDataString(_model)}:generateContent");
        request.Headers.Add("x-goog-api-key", _apiKey);
        request.Content = JsonContent.Create(requestBody);
        return request;
    }

    private async Task DelayBeforeRetryAsync(int failedAttempt, CancellationToken cancellationToken)
    {
        if (_baseRetryDelayMilliseconds == 0) return;

        var exponentialDelay = _baseRetryDelayMilliseconds * (1 << (failedAttempt - 1));
        var jitter = Random.Shared.Next(0, Math.Max(1, _baseRetryDelayMilliseconds / 2));
        await Task.Delay(TimeSpan.FromMilliseconds(exponentialDelay + jitter), cancellationToken);
    }

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    private static string GetHttpErrorMessage(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest => "Geminiへのリクエストが受理されませんでした。モデル設定を確認してください。",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "Gemini APIキーまたは利用権限を確認してください。",
        HttpStatusCode.NotFound => "指定したGeminiモデルが見つかりません。モデル設定を確認してください。",
        HttpStatusCode.TooManyRequests =>
            "Geminiの利用上限または一時的な混雑が解消しませんでした。少し待ってから再実行してください。",
        HttpStatusCode.ServiceUnavailable =>
            "Google側が一時的に混雑しています（HTTP 503）。自動再試行でも復旧しないため、少し待ってから再実行してください。",
        _ => $"Gemini APIの呼び出しに失敗しました（HTTP {(int)statusCode}）。"
    };

    private static int ReadIntSetting(string? rawValue, int defaultValue, int minimum, int maximum) =>
        int.TryParse(rawValue, out var value) ? Math.Clamp(value, minimum, maximum) : defaultValue;

    private static object[] BuildParts(GeminiChatTurn turn)
    {
        var parts = new List<object> { new { text = turn.Text.Trim() } };
        if (turn.Attachment is not { } attachment) return [.. parts];

        if (attachment.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            var text = System.Text.Encoding.UTF8.GetString(attachment.Data);
            parts.Add(new { text = $"添付ファイル「{attachment.FileName}」の内容:\n{text}" });
        }
        else
        {
            parts.Add(new
            {
                inlineData = new
                {
                    mimeType = attachment.ContentType,
                    data = Convert.ToBase64String(attachment.Data)
                }
            });
        }
        return [.. parts];
    }
}

public sealed record GeminiChatResult(
    bool IsSuccess,
    string? Answer,
    string? ErrorMessage,
    string? ModelId,
    int? InputTokens,
    int? OutputTokens)
{
    public static GeminiChatResult Failure(string message) =>
        new(false, null, message, null, null, null);
}
