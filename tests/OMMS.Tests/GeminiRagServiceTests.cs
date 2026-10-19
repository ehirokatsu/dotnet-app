using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OMMS.Services;

namespace OMMS.Tests;

public sealed class GeminiRagServiceTests
{
    [Fact]
    public async Task MissingApiKey_IsRejectedWithoutHttpCall()
    {
        var handler = new SequenceHttpHandler(_ => throw new InvalidOperationException("HTTP must not be called"));

        var result = await CreateService(handler, "").AskAsync("質問");

        Assert.False(result.IsSuccess);
        Assert.Contains("APIキー", result.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UnsupportedFile_IsRejectedWithoutHttpCall()
    {
        var handler = new SequenceHttpHandler(_ => throw new InvalidOperationException("HTTP must not be called"));

        var result = await CreateService(handler).UploadAsync(
            "malware.exe", "application/octet-stream", [1]);

        Assert.False(result.IsSuccess);
        Assert.Contains("対応していない", result.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EnsureStore_CreatesStoreWhenItDoesNotExist()
    {
        var handler = new SequenceHttpHandler(index => index switch
        {
            0 => JsonResponse("{\"fileSearchStores\":[]}"),
            1 => JsonResponse("{\"name\":\"fileSearchStores/omms-123\",\"displayName\":\"omms-rag-poc\"}"),
            _ => throw new InvalidOperationException("Unexpected HTTP request")
        });

        var result = await CreateService(handler).EnsureStoreAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("fileSearchStores/omms-123", result.Store!.Name);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("models/gemini-embedding-2",
            body.RootElement.GetProperty("embeddingModel").GetString());
    }

    [Fact]
    public async Task Upload_UsesResumableProtocolAndFinalizesBytes()
    {
        var handler = new SequenceHttpHandler(index => index switch
        {
            0 => JsonResponse("""
                {"fileSearchStores":[{"name":"fileSearchStores/omms-123","displayName":"omms-rag-poc"}]}
                """),
            1 => UploadStartResponse("https://upload.example/session-1"),
            2 => JsonResponse("{\"name\":\"operations/upload-1\",\"done\":true}"),
            _ => throw new InvalidOperationException("Unexpected HTTP request")
        });

        var result = await CreateService(handler).UploadAsync(
            "../manual.pdf", "application/pdf", [1, 2, 3]);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, handler.Requests.Count);
        var start = handler.Requests[1];
        Assert.Equal("resumable", start.Headers["X-Goog-Upload-Protocol"]);
        Assert.Equal("3", start.Headers["X-Goog-Upload-Header-Content-Length"]);
        using var startBody = JsonDocument.Parse(start.Body!);
        Assert.Equal("manual.pdf", startBody.RootElement.GetProperty("displayName").GetString());
        var upload = handler.Requests[2];
        Assert.Equal("upload, finalize", upload.Headers["X-Goog-Upload-Command"]);
        Assert.Equal([1, 2, 3], upload.BodyBytes);
    }

    [Fact]
    public async Task Ask_UsesFileSearchStoreAndParsesCitation()
    {
        var handler = new SequenceHttpHandler(index => index switch
        {
            0 => JsonResponse("""
                {"fileSearchStores":[{"name":"fileSearchStores/omms-123","displayName":"omms-rag-poc"}]}
                """),
            1 => JsonResponse("""
                {
                  "output_text":"回答です",
                  "steps":[{"type":"model_output","content":[{
                    "type":"text","text":"回答です",
                    "annotations":[{"type":"file_citation","file_name":"manual.pdf","page_number":4}]
                  }]}]
                }
                """),
            _ => throw new InvalidOperationException("Unexpected HTTP request")
        });

        var result = await CreateService(handler).AskAsync("  手順は？  ");

        Assert.True(result.IsSuccess);
        Assert.Equal("回答です", result.Answer);
        var citation = Assert.Single(result.Citations);
        Assert.Equal("manual.pdf", citation.FileName);
        Assert.Equal(4, citation.PageNumber);
        var query = handler.Requests[1];
        Assert.EndsWith("/v1beta/interactions", query.Uri.AbsoluteUri);
        using var body = JsonDocument.Parse(query.Body!);
        Assert.Equal("gemini-3.6-flash", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("手順は？", body.RootElement.GetProperty("input").GetString());
        Assert.Equal("file_search", body.RootElement.GetProperty("tools")[0]
            .GetProperty("type").GetString());
        Assert.Equal("fileSearchStores/omms-123", body.RootElement.GetProperty("tools")[0]
            .GetProperty("file_search_store_names")[0].GetString());
    }

    [Fact]
    public async Task Ask_ServiceUnavailable_ReturnsRetryableMessage()
    {
        var handler = new SequenceHttpHandler(index => index switch
        {
            0 => JsonResponse("""
                {"fileSearchStores":[{"name":"fileSearchStores/omms-123","displayName":"omms-rag-poc"}]}
                """),
            1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => throw new InvalidOperationException("Unexpected HTTP request")
        });

        var result = await CreateService(handler).AskAsync("質問");

        Assert.False(result.IsSuccess);
        Assert.Contains("一時的に混雑", result.ErrorMessage);
        Assert.Contains("HTTP 503", result.ErrorMessage);
    }

    [Fact]
    public async Task DeleteStore_UsesForceDelete()
    {
        var handler = new SequenceHttpHandler(index => index switch
        {
            0 => JsonResponse("""
                {"fileSearchStores":[{"name":"fileSearchStores/omms-123","displayName":"omms-rag-poc"}]}
                """),
            1 => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => throw new InvalidOperationException("Unexpected HTTP request")
        });

        var result = await CreateService(handler).DeleteStoreAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.EndsWith("/fileSearchStores/omms-123?force=true", handler.Requests[1].Uri.AbsoluteUri);
    }

    private static GeminiRagService CreateService(SequenceHttpHandler handler, string key = "test-key")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gemini:ApiKey"] = key,
            ["GeminiRag:Model"] = "gemini-3.6-flash",
            ["GeminiRag:StoreDisplayName"] = "omms-rag-poc"
        }).Build();
        return new GeminiRagService(new HttpClient(handler), configuration);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage UploadStartResponse(string uploadUrl)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        response.Headers.Add("X-Goog-Upload-URL", uploadUrl);
        return response;
    }
}

public sealed class SequenceHttpHandler(Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bodyBytes = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var allHeaders = request.Content is null
            ? request.Headers
            : request.Headers.Concat(request.Content.Headers);
        var headers = allHeaders
            .ToDictionary(header => header.Key, header => string.Join(", ", header.Value),
                StringComparer.OrdinalIgnoreCase);
        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri!,
            bodyBytes is null ? null : Encoding.UTF8.GetString(bodyBytes),
            bodyBytes,
            headers));
        return responseFactory(Requests.Count - 1);
    }
}

public sealed record CapturedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Body,
    byte[]? BodyBytes,
    IReadOnlyDictionary<string, string> Headers);
