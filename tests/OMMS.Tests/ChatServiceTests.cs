using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.Configuration;
using OMMS.Models;
using OMMS.Services;

namespace OMMS.Tests;

public sealed class GeminiChatServiceTests
{
    [Fact]
    public async Task EmptyInput_IsRejectedWithoutHttpCall()
    {
        var handler = NoHttpHandler();

        var result = await GeminiService(handler).SendAsync("   ");

        Assert.False(result.IsSuccess);
        Assert.Contains("質問を入力してください", result.ErrorMessage);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task OversizedInput_IsRejectedWithoutHttpCall()
    {
        var handler = NoHttpHandler();

        var result = await GeminiService(handler).SendAsync(
            new string('x', ChatInputPolicy.MaxLength + 1));

        Assert.False(result.IsSuccess);
        Assert.Contains("10,000文字以内", result.ErrorMessage);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task MissingApiKey_IsRejectedWithoutHttpCall()
    {
        var handler = NoHttpHandler();

        var result = await GeminiService(handler, "").SendAsync("test");

        Assert.False(result.IsSuccess);
        Assert.Contains("APIキー", result.ErrorMessage);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task SuccessfulResponse_IsParsedAndRequestIsCorrect()
    {
        var handler = new FakeHttpHandler(_ => JsonResponse("""
            {"candidates":[{"content":{"parts":[{"text":"first"},{"thought":true,"text":"internal"},{"text":"second"}]}}],"usageMetadata":{"promptTokenCount":12,"candidatesTokenCount":4}}
            """));

        var result = await GeminiService(handler).SendAsync("  hello  ");

        Assert.True(result.IsSuccess);
        Assert.Equal($"first{Environment.NewLine}second", result.Answer);
        Assert.Equal(12, result.InputTokens);
        Assert.Equal(4, result.OutputTokens);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.EndsWith("/gemini-3.6-flash:generateContent", handler.LastUri!.AbsoluteUri);
        Assert.Equal("test-key", handler.LastApiKey);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("hello", body.RootElement.GetProperty("contents")[0]
            .GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Contains("日本語", body.RootElement.GetProperty("systemInstruction")
            .GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task MultiTurnRequest_IncludesConversationHistory()
    {
        var handler = SuccessfulHandler("Hiroです");

        var result = await GeminiService(handler).SendAsync([
            new GeminiChatTurn("user", "私の名前はHiroです"),
            new GeminiChatTurn("model", "覚えました"),
            new GeminiChatTurn("user", "私の名前は？")
        ]);

        Assert.True(result.IsSuccess);
        using var body = JsonDocument.Parse(handler.LastBody!);
        var contents = body.RootElement.GetProperty("contents");
        Assert.Equal(3, contents.GetArrayLength());
        Assert.Equal("user", contents[0].GetProperty("role").GetString());
        Assert.Equal("model", contents[1].GetProperty("role").GetString());
        Assert.Equal("私の名前は？", contents[2].GetProperty("parts")[0]
            .GetProperty("text").GetString());
    }

    [Fact]
    public async Task PdfAttachment_IsSentAsInlineBase64Data()
    {
        var handler = SuccessfulHandler();
        var attachment = new GeminiAttachment
        {
            FileName = "sample.pdf",
            ContentType = "application/pdf",
            Data = [1, 2, 3]
        };

        var result = await GeminiService(handler).SendAsync([
            new GeminiChatTurn("user", "要約して", attachment)
        ]);

        Assert.True(result.IsSuccess);
        using var body = JsonDocument.Parse(handler.LastBody!);
        var parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        var inlineData = parts[1].GetProperty("inlineData");
        Assert.Equal("application/pdf", inlineData.GetProperty("mimeType").GetString());
        Assert.Equal("AQID", inlineData.GetProperty("data").GetString());
    }

    [Fact]
    public async Task TextAttachment_IsSentAsTextPart()
    {
        var handler = SuccessfulHandler();
        var attachment = new GeminiAttachment
        {
            FileName = "sample.txt",
            ContentType = "text/plain",
            Data = Encoding.UTF8.GetBytes("添付本文")
        };

        var result = await GeminiService(handler).SendAsync([
            new GeminiChatTurn("user", "要約して", attachment)
        ]);

        Assert.True(result.IsSuccess);
        using var body = JsonDocument.Parse(handler.LastBody!);
        var text = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[1]
            .GetProperty("text").GetString();
        Assert.Contains("sample.txt", text);
        Assert.Contains("添付本文", text);
    }

    [Fact]
    public async Task UnsupportedAttachment_IsRejectedWithoutHttpCall()
    {
        var handler = NoHttpHandler();
        var attachment = new GeminiAttachment
        {
            FileName = "malware.exe",
            ContentType = "application/octet-stream",
            Data = [1]
        };

        var result = await GeminiService(handler).SendAsync([
            new GeminiChatTurn("user", "解析して", attachment)
        ]);

        Assert.False(result.IsSuccess);
        Assert.Contains("のみ添付", result.ErrorMessage);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task TooManyRequests_ReturnsUsageLimitMessage()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var result = await GeminiService(handler).SendAsync("test");

        Assert.False(result.IsSuccess);
        Assert.Contains("利用上限", result.ErrorMessage);
    }

    [Fact]
    public async Task ServiceUnavailable_IsRetriedAndEventuallySucceeds()
    {
        var responseCount = 0;
        var handler = new FakeHttpHandler(_ => ++responseCount < 3
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : JsonResponse("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"recovered\"}]}}]}"));

        var result = await GeminiService(handler).SendAsync("test");

        Assert.True(result.IsSuccess);
        Assert.Equal("recovered", result.Answer);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task ServiceUnavailable_AfterRetriesReturnsHelpfulMessage()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await GeminiService(handler).SendAsync("test");

        Assert.False(result.IsSuccess);
        Assert.Contains("一時的に混雑", result.ErrorMessage);
        Assert.Contains("HTTP 503", result.ErrorMessage);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task MissingAnswerBody_ReturnsReadableError()
    {
        var handler = new FakeHttpHandler(_ => JsonResponse(
            "{\"candidates\":[{\"content\":{\"parts\":[1,{}]}}],\"usageMetadata\":{\"promptTokenCount\":\"unknown\"}}"));

        var result = await GeminiService(handler).SendAsync("test");

        Assert.False(result.IsSuccess);
        Assert.Contains("回答本文", result.ErrorMessage);
    }

    [Fact]
    public async Task UnexpectedTopLevelJson_ReturnsReadableError()
    {
        var handler = new FakeHttpHandler(_ => JsonResponse("[]"));

        var result = await GeminiService(handler).SendAsync("test");

        Assert.False(result.IsSuccess);
        Assert.Contains("回答本文", result.ErrorMessage);
    }

    [Fact]
    public async Task MalformedJson_ReturnsReadableError()
    {
        var handler = new FakeHttpHandler(_ => JsonResponse("not JSON"));

        var result = await GeminiService(handler).SendAsync("test");

        Assert.False(result.IsSuccess);
        Assert.Contains("読み取れません", result.ErrorMessage);
    }

    [Fact]
    public async Task Timeout_ReturnsTimeoutMessage()
    {
        var handler = new FakeHttpHandler(_ => throw new TaskCanceledException("timeout"));

        var result = await GeminiService(handler).SendAsync("test");

        Assert.False(result.IsSuccess);
        Assert.Contains("タイムアウト", result.ErrorMessage);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            entries.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value))).Build();

    private static GeminiChatService GeminiService(FakeHttpHandler handler, string? key = "test-key") =>
        new(new HttpClient(handler), Configuration(
            ("Gemini:ApiKey", key ?? ""),
            ("Gemini:Model", "gemini-3.6-flash"),
            ("Gemini:RetryMaxAttempts", "3"),
            ("Gemini:RetryBaseDelayMilliseconds", "0")));

    private static FakeHttpHandler NoHttpHandler() =>
        new(_ => throw new InvalidOperationException("HTTP must not be called"));

    private static FakeHttpHandler SuccessfulHandler(string answer = "ok") => new(_ =>
        JsonResponse($"{{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":\"{answer}\"}}]}}}}]}}"));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

public sealed class BedrockChatServiceTests
{
    [Fact]
    public async Task EmptyAndOversizedInput_AreRejectedWithoutApiCall()
    {
        var client = DispatchProxy.Create<IAmazonBedrockRuntime, FakeBedrockProxy>();
        var service = new BedrockChatService(client,
            Configuration(("Bedrock:ModelId", "jp.amazon.nova-2-lite-v1:0")));

        var empty = await service.SendAsync(" ");
        var oversized = await service.SendAsync(new string('x', ChatInputPolicy.MaxLength + 1));

        Assert.False(empty.IsSuccess);
        Assert.Contains("質問を入力", empty.ErrorMessage);
        Assert.False(oversized.IsSuccess);
        Assert.Contains("10,000文字以内", oversized.ErrorMessage);
        Assert.Equal(0, ((FakeBedrockProxy)client).RequestCount);
    }

    [Fact]
    public async Task SuccessfulResponse_IsParsedAndRequestIsCorrect()
    {
        var client = DispatchProxy.Create<IAmazonBedrockRuntime, FakeBedrockProxy>();
        var service = new BedrockChatService(client, Configuration(
            ("Bedrock:ModelId", "jp.amazon.nova-2-lite-v1:0"),
            ("Bedrock:MaxOutputTokens", "40")));

        var result = await service.SendAsync("  hello  ");

        var proxy = (FakeBedrockProxy)client;
        var request = Assert.IsType<ConverseRequest>(proxy.LastRequest);
        Assert.True(result.IsSuccess);
        Assert.Equal("success", result.Answer);
        Assert.Equal(1, proxy.RequestCount);
        Assert.Equal("jp.amazon.nova-2-lite-v1:0", request.ModelId);
        Assert.Equal("hello", request.Messages[0].Content[0].Text);
        Assert.Equal(40, request.InferenceConfig.MaxTokens);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            entries.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value))).Build();
}

public sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    public HttpMethod? LastMethod { get; private set; }
    public Uri? LastUri { get; private set; }
    public string? LastApiKey { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        LastMethod = request.Method;
        LastUri = request.RequestUri;
        LastApiKey = request.Headers.GetValues("x-goog-api-key").Single();
        LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        return responseFactory(request);
    }
}

public class FakeBedrockProxy : DispatchProxy
{
    public int RequestCount { get; private set; }
    public ConverseRequest? LastRequest { get; private set; }

    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if (method?.Name != nameof(IAmazonBedrockRuntime.ConverseAsync))
            throw new NotSupportedException(method?.Name);

        RequestCount++;
        LastRequest = (ConverseRequest)arguments![0]!;
        return Task.FromResult(new ConverseResponse
        {
            Output = new ConverseOutput
            {
                Message = new Message
                {
                    Role = ConversationRole.Assistant,
                    Content = [new ContentBlock { Text = "success" }]
                }
            }
        });
    }
}
