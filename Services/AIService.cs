using System.Net.Http.Json;
using System.Text.Json;

namespace OMMS.Services;

public class AIService(HttpClient httpClient, IConfiguration configuration)
{
    private const string ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";
    private readonly string _apiKey = configuration["Gemini:ApiKey"] ?? string.Empty;
    private readonly string _model = configuration["Gemini:Model"] ?? "gemini-3.5-flash";

    public async Task<string> AnalyzeTextAsync(string inputText)
    {
        if (string.IsNullOrWhiteSpace(inputText))
        {
            return "解析するテキストを入力してください。";
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return "Gemini APIキーが設定されていません。開発環境のシークレットに Gemini:ApiKey を設定してください。";
        }

        var requestBody = new
        {
            systemInstruction = new
            {
                parts = new[]
                {
                    new { text = "あなたは業務支援アシスタントです。入力文を日本語で簡潔に要約し、重要なポイントと次の対応案を箇条書きで示してください。" }
                }
            },
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = inputText } }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/{_model}:generateContent");
        request.Headers.Add("x-goog-api-key", _apiKey);
        request.Content = JsonContent.Create(requestBody);

        try
        {
            using var response = await httpClient.SendAsync(request);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return $"Gemini APIの呼び出しに失敗しました（HTTP {(int)response.StatusCode}）。{GetErrorMessage(responseJson)}";
            }

            using var document = JsonDocument.Parse(responseJson);
            var text = document.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            return string.IsNullOrWhiteSpace(text)
                ? "Geminiから解析結果を取得できませんでした。"
                : text;
        }
        catch (HttpRequestException)
        {
            return "Gemini APIへ接続できませんでした。ネットワーク接続を確認してください。";
        }
        catch (JsonException)
        {
            return "Gemini APIの応答を読み取れませんでした。モデル名またはAPI設定を確認してください。";
        }
    }

    private static string GetErrorMessage(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            return document.RootElement
                .GetProperty("error")
                .GetProperty("message")
                .GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
