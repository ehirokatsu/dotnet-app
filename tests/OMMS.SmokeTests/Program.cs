using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.Configuration;
using OMMS.Models;
using OMMS.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Gemini: empty input", TestGeminiEmptyInput),
    ("Gemini: oversized input", TestGeminiOversizedInput),
    ("Gemini: missing API key", TestGeminiMissingKey),
    ("Gemini: request and successful response", TestGeminiSuccess),
    ("Gemini: multi-turn request", TestGeminiMultiTurn),
    ("Gemini: PDF attachment request", TestGeminiPdfAttachment),
    ("Gemini: text attachment request", TestGeminiTextAttachment),
    ("Gemini: attachment validation", TestGeminiAttachmentValidation),
    ("Gemini: HTTP error", TestGeminiHttpError),
    ("Gemini: 503 retry and recovery", TestGeminiServiceUnavailableRecovery),
    ("Gemini: 503 retry exhausted", TestGeminiServiceUnavailableExhausted),
    ("Gemini: unexpected response", TestGeminiUnexpectedResponse),
    ("Gemini: unexpected top-level JSON", TestGeminiUnexpectedRoot),
    ("Gemini: malformed JSON", TestGeminiMalformedJson),
    ("Gemini: timeout", TestGeminiTimeout),
    ("Bedrock: empty and oversized input", TestBedrockValidation),
    ("Bedrock: request and successful response", TestBedrockSuccess)
};

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
    new ConfigurationBuilder().AddInMemoryCollection(
        entries.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value))).Build();

static GeminiChatService GeminiService(FakeHttpHandler handler, string? key = "test-key") =>
    new(new HttpClient(handler), Configuration(
        ("Gemini:ApiKey", key ?? ""),
        ("Gemini:Model", "gemini-3.6-flash"),
        ("Gemini:RetryMaxAttempts", "3"),
        ("Gemini:RetryBaseDelayMilliseconds", "0")));

static Task TestGeminiEmptyInput() => TestGeminiValidation("   ", "質問を入力してください。");
static Task TestGeminiOversizedInput() =>
    TestGeminiValidation(new string('x', ChatInputPolicy.MaxLength + 1), "10,000文字以内");

static async Task TestGeminiValidation(string input, string expected)
{
    var handler = new FakeHttpHandler(_ => throw new Exception("HTTP must not be called"));
    var result = await GeminiService(handler).SendAsync(input);
    Check(!result.IsSuccess && result.ErrorMessage!.Contains(expected), "Validation error missing");
    Check(handler.RequestCount == 0, "Validation sent a request");
}

static async Task TestGeminiMissingKey()
{
    var handler = new FakeHttpHandler(_ => throw new Exception("HTTP must not be called"));
    var result = await GeminiService(handler, "").SendAsync("test");
    Check(!result.IsSuccess && result.ErrorMessage!.Contains("APIキー"), "Missing-key error missing");
    Check(handler.RequestCount == 0, "Missing key sent a request");
}

static async Task TestGeminiSuccess()
{
    var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {"candidates":[{"content":{"parts":[{"text":"first"},{"thought":true,"text":"internal"},{"text":"second"}]}}],"usageMetadata":{"promptTokenCount":12,"candidatesTokenCount":4}}
            """, Encoding.UTF8, "application/json")
    });
    var result = await GeminiService(handler).SendAsync("  hello  ");
    Check(result.IsSuccess && result.Answer == $"first{Environment.NewLine}second", "Wrong Gemini answer");
    Check(result.InputTokens == 12 && result.OutputTokens == 4, "Wrong Gemini usage");
    Check(handler.LastMethod == HttpMethod.Post, "Wrong HTTP method");
    Check(handler.LastUri!.AbsoluteUri.EndsWith("/gemini-3.6-flash:generateContent"), "Wrong endpoint");
    Check(handler.LastApiKey == "test-key", "API key header missing");
    using var body = JsonDocument.Parse(handler.LastBody!);
    Check(body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0]
        .GetProperty("text").GetString() == "hello", "Prompt not trimmed");
    Check(body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0]
        .GetProperty("text").GetString()!.Contains("日本語"), "System instruction missing");
}

static async Task TestGeminiHttpError()
{
    var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
    var result = await GeminiService(handler).SendAsync("test");
    Check(!result.IsSuccess && result.ErrorMessage!.Contains("利用上限"), "Wrong 429 handling");
}

static async Task TestGeminiServiceUnavailableRecovery()
{
    var responseCount = 0;
    var handler = new FakeHttpHandler(_ => ++responseCount < 3
        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"recovered\"}]}}]}")
        });

    var result = await GeminiService(handler).SendAsync("test");
    Check(result.IsSuccess && result.Answer == "recovered", "503 did not recover after retry");
    Check(handler.RequestCount == 3, "503 retry count is wrong");
}

static async Task TestGeminiServiceUnavailableExhausted()
{
    var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    var result = await GeminiService(handler).SendAsync("test");
    Check(!result.IsSuccess && result.ErrorMessage!.Contains("一時的に混雑") &&
          result.ErrorMessage.Contains("HTTP 503"), "Wrong 503 exhausted message");
    Check(handler.RequestCount == 3, "503 exhausted retry count is wrong");
}

static async Task TestGeminiMultiTurn()
{
    var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hiroです\"}]}}]}")
    });
    var result = await GeminiService(handler).SendAsync([
        new GeminiChatTurn("user", "私の名前はHiroです"),
        new GeminiChatTurn("model", "覚えました"),
        new GeminiChatTurn("user", "私の名前は？")
    ]);
    Check(result.IsSuccess, "Multi-turn request failed");
    using var body = JsonDocument.Parse(handler.LastBody!);
    var contents = body.RootElement.GetProperty("contents");
    Check(contents.GetArrayLength() == 3, "Conversation history missing");
    Check(contents[0].GetProperty("role").GetString() == "user", "First role is wrong");
    Check(contents[1].GetProperty("role").GetString() == "model", "Model role is wrong");
    Check(contents[2].GetProperty("parts")[0].GetProperty("text").GetString() == "私の名前は？",
        "Latest question missing");
}

static async Task TestGeminiPdfAttachment()
{
    var handler = SuccessfulGeminiHandler();
    var attachment = new GeminiAttachment
    {
        FileName = "sample.pdf",
        ContentType = "application/pdf",
        Data = [1, 2, 3]
    };
    var result = await GeminiService(handler).SendAsync([
        new GeminiChatTurn("user", "要約して", attachment)
    ]);
    Check(result.IsSuccess, "PDF attachment failed");
    using var body = JsonDocument.Parse(handler.LastBody!);
    var parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");
    Check(parts.GetArrayLength() == 2, "PDF part missing");
    var inlineData = parts[1].GetProperty("inlineData");
    Check(inlineData.GetProperty("mimeType").GetString() == "application/pdf", "Wrong PDF MIME type");
    Check(inlineData.GetProperty("data").GetString() == "AQID", "Wrong PDF Base64 data");
}

static async Task TestGeminiTextAttachment()
{
    var handler = SuccessfulGeminiHandler();
    var attachment = new GeminiAttachment
    {
        FileName = "sample.txt",
        ContentType = "text/plain",
        Data = Encoding.UTF8.GetBytes("添付本文")
    };
    var result = await GeminiService(handler).SendAsync([
        new GeminiChatTurn("user", "要約して", attachment)
    ]);
    Check(result.IsSuccess, "Text attachment failed");
    using var body = JsonDocument.Parse(handler.LastBody!);
    var text = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[1]
        .GetProperty("text").GetString();
    Check(text!.Contains("sample.txt") && text.Contains("添付本文"), "Text attachment content missing");
}

static async Task TestGeminiAttachmentValidation()
{
    var handler = new FakeHttpHandler(_ => throw new Exception("HTTP must not be called"));
    var attachment = new GeminiAttachment
    {
        FileName = "malware.exe",
        ContentType = "application/octet-stream",
        Data = [1]
    };
    var result = await GeminiService(handler).SendAsync([
        new GeminiChatTurn("user", "解析して", attachment)
    ]);
    Check(!result.IsSuccess && result.ErrorMessage!.Contains("のみ添付"), "Invalid attachment accepted");
    Check(handler.RequestCount == 0, "Invalid attachment sent a request");
}

static FakeHttpHandler SuccessfulGeminiHandler() => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]}}]}")
});

static async Task TestGeminiUnexpectedResponse()
{
    var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[1,{}]}}],\"usageMetadata\":{\"promptTokenCount\":\"unknown\"}}")
    });
    var result = await GeminiService(handler).SendAsync("test");
    Check(!result.IsSuccess && result.ErrorMessage!.Contains("回答本文"), "Unexpected JSON was not handled");
}

static async Task TestGeminiMalformedJson()
{
    var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("not JSON")
    });
    var result = await GeminiService(handler).SendAsync("test");
    Check(!result.IsSuccess && result.ErrorMessage!.Contains("読み取れません"), "Malformed JSON was not handled");
}

static async Task TestGeminiUnexpectedRoot()
{
    var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("[]")
    });
    var result = await GeminiService(handler).SendAsync("test");
    Check(!result.IsSuccess && result.ErrorMessage!.Contains("回答本文"), "Unexpected root was not handled");
}

static async Task TestGeminiTimeout()
{
    var handler = new FakeHttpHandler(_ => throw new TaskCanceledException("timeout"));
    var result = await GeminiService(handler).SendAsync("test");
    Check(!result.IsSuccess && result.ErrorMessage!.Contains("タイムアウト"), "Timeout was not handled");
}

static async Task TestBedrockValidation()
{
    var client = DispatchProxy.Create<IAmazonBedrockRuntime, FakeBedrockProxy>();
    var service = new BedrockChatService(client, Configuration(("Bedrock:ModelId", "jp.amazon.nova-2-lite-v1:0")));
    var empty = await service.SendAsync(" ");
    var oversized = await service.SendAsync(new string('x', ChatInputPolicy.MaxLength + 1));
    Check(!empty.IsSuccess && empty.ErrorMessage!.Contains("質問を入力"), "Bedrock empty input accepted");
    Check(!oversized.IsSuccess && oversized.ErrorMessage!.Contains("10,000文字以内"), "Bedrock oversized input accepted");
    Check(((FakeBedrockProxy)client).RequestCount == 0, "Bedrock validation called API");
}

static async Task TestBedrockSuccess()
{
    var client = DispatchProxy.Create<IAmazonBedrockRuntime, FakeBedrockProxy>();
    var service = new BedrockChatService(client, Configuration(
        ("Bedrock:ModelId", "jp.amazon.nova-2-lite-v1:0"), ("Bedrock:MaxOutputTokens", "40")));
    var result = await service.SendAsync("  hello  ");
    var proxy = (FakeBedrockProxy)client;
    var request = proxy.LastRequest ?? throw new Exception("Bedrock request missing");
    Check(result.IsSuccess && result.Answer == "success", "Wrong Bedrock answer");
    Check(proxy.RequestCount == 1 && request.ModelId == "jp.amazon.nova-2-lite-v1:0", "Wrong Bedrock model");
    Check(request.Messages[0].Content[0].Text == "hello", "Bedrock prompt not trimmed");
    Check(request.InferenceConfig.MaxTokens == 40, "Bedrock output limit ignored");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

public sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
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
