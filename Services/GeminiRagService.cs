using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OMMS.Services;

public sealed class GeminiRagService(HttpClient httpClient, IConfiguration configuration)
{
    private const string ApiBase = "https://generativelanguage.googleapis.com/v1beta";
    private const string UploadBase = "https://generativelanguage.googleapis.com/upload/v1beta";
    private readonly string _apiKey = configuration["Gemini:ApiKey"] ?? string.Empty;
    private readonly string _model = configuration["GeminiRag:Model"] ?? "gemini-3.6-flash";
    private readonly string _storeDisplayName = configuration["GeminiRag:StoreDisplayName"] ?? "omms-rag-poc";

    private static readonly IReadOnlyDictionary<string, string> SupportedContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".txt"] = "text/plain",
            [".md"] = "text/markdown",
            [".csv"] = "text/csv",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".ppt"] = "application/vnd.ms-powerpoint",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation"
        };

    public const long MaxFileSize = 10 * 1024 * 1024;

    public async Task<GeminiRagStore?> GetStoreAsync(CancellationToken cancellationToken = default)
    {
        if (!HasApiKey(out _)) return null;
        using var request = CreateRequest(HttpMethod.Get, $"{ApiBase}/fileSearchStores");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var json = await ParseAsync(response, cancellationToken);
        if (!json.RootElement.TryGetProperty("fileSearchStores", out var stores)) return null;
        foreach (var store in stores.EnumerateArray())
        {
            if (ReadString(store, "displayName") == _storeDisplayName)
                return ParseStore(store);
        }
        return null;
    }

    public async Task<GeminiRagStoreResult> EnsureStoreAsync(CancellationToken cancellationToken = default)
    {
        if (!HasApiKey(out var error)) return GeminiRagStoreResult.Failure(error);
        try
        {
            if (await GetStoreAsync(cancellationToken) is { } existing)
                return GeminiRagStoreResult.Success(existing);

            using var request = CreateRequest(HttpMethod.Post, $"{ApiBase}/fileSearchStores");
            request.Content = JsonContent.Create(new
            {
                displayName = _storeDisplayName,
                embeddingModel = "models/gemini-embedding-2"
            });
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return GeminiRagStoreResult.Failure(ApiError("RAGストアを作成", response.StatusCode));
            using var json = await ParseAsync(response, cancellationToken);
            return GeminiRagStoreResult.Success(ParseStore(json.RootElement));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return GeminiRagStoreResult.Failure("Google File Searchへ接続できませんでした。");
        }
    }

    public async Task<GeminiRagActionResult> UploadAsync(
        string fileName, string contentType, byte[] data, CancellationToken cancellationToken = default)
    {
        if (!HasApiKey(out var error)) return GeminiRagActionResult.Failure(error);
        if (!TryResolveContentType(fileName, out var expectedContentType) ||
            !string.Equals(contentType, expectedContentType, StringComparison.OrdinalIgnoreCase))
            return GeminiRagActionResult.Failure("対応していないファイル形式です。");
        if (data.Length == 0 || data.LongLength > MaxFileSize)
            return GeminiRagActionResult.Failure("ファイルは1バイト以上10MB以内にしてください。");

        var storeResult = await EnsureStoreAsync(cancellationToken);
        if (!storeResult.IsSuccess) return GeminiRagActionResult.Failure(storeResult.ErrorMessage!);

        try
        {
            var safeName = Path.GetFileName(fileName);
            var startUrl = $"{UploadBase}/{storeResult.Store!.Name}:uploadToFileSearchStore";
            using var start = CreateRequest(HttpMethod.Post, startUrl);
            start.Headers.Add("X-Goog-Upload-Protocol", "resumable");
            start.Headers.Add("X-Goog-Upload-Command", "start");
            start.Headers.Add("X-Goog-Upload-Header-Content-Length", data.Length.ToString());
            start.Headers.Add("X-Goog-Upload-Header-Content-Type", contentType);
            start.Content = JsonContent.Create(new { displayName = safeName });
            using var startResponse = await httpClient.SendAsync(start, cancellationToken);
            if (!startResponse.IsSuccessStatusCode ||
                !startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var values))
                return GeminiRagActionResult.Failure(ApiError("アップロードを開始", startResponse.StatusCode));

            using var upload = new HttpRequestMessage(HttpMethod.Post, values.Single());
            upload.Headers.Add("X-Goog-Upload-Offset", "0");
            upload.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
            upload.Content = new ByteArrayContent(data);
            upload.Content.Headers.ContentType = new(contentType);
            using var uploadResponse = await httpClient.SendAsync(upload, cancellationToken);
            if (!uploadResponse.IsSuccessStatusCode)
                return GeminiRagActionResult.Failure(ApiError("ファイルを登録", uploadResponse.StatusCode));

            using var json = await ParseAsync(uploadResponse, cancellationToken);
            await WaitForOperationAsync(json.RootElement, cancellationToken);
            return GeminiRagActionResult.Success("ファイルをRAGストアへ登録しました。");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            return GeminiRagActionResult.Failure("ファイルの登録処理に失敗しました。");
        }
    }

    public async Task<IReadOnlyList<GeminiRagDocument>> ListDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!HasApiKey(out _)) return [];
        try
        {
            var store = await GetStoreAsync(cancellationToken);
            if (store is null) return [];
            using var request = CreateRequest(HttpMethod.Get, $"{ApiBase}/{store.Name}/documents");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return [];
            using var json = await ParseAsync(response, cancellationToken);
            if (!json.RootElement.TryGetProperty("documents", out var documents)) return [];
            return documents.EnumerateArray().Select(document => new GeminiRagDocument(
                ReadString(document, "name") ?? "",
                ReadString(document, "displayName") ?? ReadString(document, "name") ?? "",
                ReadString(document, "state") ?? "")).ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return [];
        }
    }

    public async Task<GeminiRagQueryResult> AskAsync(
        string question, CancellationToken cancellationToken = default)
    {
        if (!HasApiKey(out var error)) return GeminiRagQueryResult.Failure(error);
        if (ChatInputPolicy.Validate(question) is { } validationError)
            return GeminiRagQueryResult.Failure(validationError);
        try
        {
            var store = await GetStoreAsync(cancellationToken);
            if (store is null) return GeminiRagQueryResult.Failure("先にファイルを登録してください。");
            using var request = CreateRequest(HttpMethod.Post, $"{ApiBase}/interactions");
            request.Content = JsonContent.Create(new
            {
                model = _model,
                input = question.Trim(),
                tools = new[] { new { type = "file_search", file_search_store_names = new[] { store.Name } } }
            });
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return GeminiRagQueryResult.Failure(ApiError("RAG検索", response.StatusCode));
            using var json = await ParseAsync(response, cancellationToken);
            return ParseQueryResult(json.RootElement);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return GeminiRagQueryResult.Failure("RAG検索の応答を読み取れませんでした。");
        }
    }

    public async Task<GeminiRagActionResult> DeleteStoreAsync(CancellationToken cancellationToken = default)
    {
        if (!HasApiKey(out var error)) return GeminiRagActionResult.Failure(error);
        try
        {
            var store = await GetStoreAsync(cancellationToken);
            if (store is null) return GeminiRagActionResult.Success("削除対象のRAGストアはありません。");
            using var request = CreateRequest(HttpMethod.Delete, $"{ApiBase}/{store.Name}?force=true");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? GeminiRagActionResult.Success("Google側のRAGストアを削除しました。")
                : GeminiRagActionResult.Failure(ApiError("RAGストアを削除", response.StatusCode));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return GeminiRagActionResult.Failure("Google側のRAGストアを削除できませんでした。");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-goog-api-key", _apiKey);
        return request;
    }

    private bool HasApiKey(out string error)
    {
        error = "Gemini APIキーが設定されていません。";
        return !string.IsNullOrWhiteSpace(_apiKey);
    }

    public static bool TryResolveContentType(string fileName, out string contentType) =>
        SupportedContentTypes.TryGetValue(Path.GetExtension(fileName), out contentType!);

    private async Task WaitForOperationAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("name", out var nameElement)) return;
        var name = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("operations/", StringComparison.Ordinal)) return;
        if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True) return;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            using var request = CreateRequest(HttpMethod.Get, $"{ApiBase}/{name}");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) continue;
            using var json = await ParseAsync(response, cancellationToken);
            if (json.RootElement.TryGetProperty("error", out _))
                throw new InvalidOperationException("索引作成に失敗しました。");
            if (json.RootElement.TryGetProperty("done", out done) && done.ValueKind == JsonValueKind.True) return;
        }
        throw new InvalidOperationException("索引作成がタイムアウトしました。");
    }

    private static GeminiRagQueryResult ParseQueryResult(JsonElement root)
    {
        var texts = new List<string>();
        var citations = new HashSet<GeminiRagCitation>();
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            texts.Add(outputText.GetString()!);
        if (root.TryGetProperty("steps", out var steps))
        {
            foreach (var step in steps.EnumerateArray())
            {
                if (!step.TryGetProperty("content", out var content)) continue;
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        texts.Add(text.GetString()!);
                    if (!block.TryGetProperty("annotations", out var annotations)) continue;
                    foreach (var annotation in annotations.EnumerateArray())
                    {
                        if (ReadString(annotation, "type") != "file_citation") continue;
                        citations.Add(new GeminiRagCitation(
                            ReadString(annotation, "file_name") ?? "参照文書",
                            annotation.TryGetProperty("page_number", out var page) && page.TryGetInt32(out var number)
                                ? number : null));
                    }
                }
            }
        }
        var answer = string.Join(Environment.NewLine, texts.Where(text => !string.IsNullOrWhiteSpace(text)).Distinct());
        return string.IsNullOrWhiteSpace(answer)
            ? GeminiRagQueryResult.Failure("Geminiから回答を取得できませんでした。")
            : GeminiRagQueryResult.Success(answer, [.. citations]);
    }

    private static GeminiRagStore ParseStore(JsonElement root) => new(
        ReadString(root, "name") ?? throw new JsonException("Store name is missing."),
        ReadString(root, "displayName") ?? "");
    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    private static string ApiError(string action, HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests =>
            $"{action}できませんでした。無料枠の利用上限に達した可能性があります（HTTP 429）。",
        HttpStatusCode.ServiceUnavailable =>
            $"{action}できませんでした。Google側が一時的に混雑しています（HTTP 503）。少し待って再実行してください。",
        _ => $"{action}できませんでした（HTTP {(int)status}）。APIキーと利用条件を確認してください。"
    };
}

public sealed record GeminiRagStore(string Name, string DisplayName);
public sealed record GeminiRagDocument(string Name, string DisplayName, string State);
public sealed record GeminiRagCitation(string FileName, int? PageNumber);
public sealed record GeminiRagStoreResult(bool IsSuccess, GeminiRagStore? Store, string? ErrorMessage)
{
    public static GeminiRagStoreResult Success(GeminiRagStore store) => new(true, store, null);
    public static GeminiRagStoreResult Failure(string error) => new(false, null, error);
}
public sealed record GeminiRagActionResult(bool IsSuccess, string? Message, string? ErrorMessage)
{
    public static GeminiRagActionResult Success(string message) => new(true, message, null);
    public static GeminiRagActionResult Failure(string error) => new(false, null, error);
}
public sealed record GeminiRagQueryResult(bool IsSuccess, string? Answer, IReadOnlyList<GeminiRagCitation> Citations, string? ErrorMessage)
{
    public static GeminiRagQueryResult Success(string answer, IReadOnlyList<GeminiRagCitation> citations) => new(true, answer, citations, null);
    public static GeminiRagQueryResult Failure(string error) => new(false, null, [], error);
}
