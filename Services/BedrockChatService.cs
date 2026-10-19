using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;

namespace OMMS.Services;

public sealed class BedrockChatService(
    IAmazonBedrockRuntime bedrockClient,
    IConfiguration configuration)
{
    private const string DefaultSystemPrompt =
        "あなたは企業内業務を支援するアシスタントです。日本語で正確かつ簡潔に回答してください。";

    private readonly string _modelId = configuration["Bedrock:ModelId"] ?? "jp.amazon.nova-2-lite-v1:0";
    private readonly int _maxOutputTokens = configuration.GetValue("Bedrock:MaxOutputTokens", 800);

    public async Task<BedrockChatResult> SendAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        if (ChatInputPolicy.Validate(userMessage) is { } validationError)
        {
            return BedrockChatResult.Failure(validationError);
        }

        try
        {
            var request = new ConverseRequest
            {
                ModelId = _modelId,
                System =
                [
                    new SystemContentBlock { Text = DefaultSystemPrompt }
                ],
                Messages =
                [
                    new Message
                    {
                        Role = ConversationRole.User,
                        Content = [new ContentBlock { Text = userMessage.Trim() }]
                    }
                ],
                InferenceConfig = new InferenceConfiguration
                {
                    MaxTokens = _maxOutputTokens,
                    Temperature = 0.2f
                }
            };

            var response = await bedrockClient.ConverseAsync(request, cancellationToken);
            var answer = string.Join(
                Environment.NewLine,
                response.Output?.Message?.Content
                    .Where(content => !string.IsNullOrWhiteSpace(content.Text))
                    .Select(content => content.Text) ?? []);

            if (string.IsNullOrWhiteSpace(answer))
            {
                return BedrockChatResult.Failure("Bedrockから回答本文を取得できませんでした。");
            }

            return new BedrockChatResult(
                true,
                answer,
                null,
                _modelId,
                response.Usage?.InputTokens,
                response.Usage?.OutputTokens);
        }
        catch (AmazonServiceException exception)
        {
            return BedrockChatResult.Failure(GetSafeErrorMessage(exception));
        }
        catch (AmazonClientException)
        {
            return BedrockChatResult.Failure(
                "AWS認証情報を取得できないか、AWSへ接続できませんでした。認証情報とネットワークを確認してください。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BedrockChatResult.Failure("Bedrockの応答がタイムアウトしました。少し待ってから再実行してください。");
        }
        catch (InvalidOperationException)
        {
            return BedrockChatResult.Failure(
                "AWS認証情報を取得できませんでした。AWSプロファイルまたは環境変数を確認してください。");
        }

    }

    private static string GetSafeErrorMessage(AmazonServiceException exception) =>
        exception.ErrorCode switch
        {
            "AccessDeniedException" =>
                "Bedrockの実行権限がありません。IAM権限とモデルの利用可否を確認してください。",
            "ResourceNotFoundException" =>
                "指定したBedrockモデルが見つかりません。リージョンとモデルIDを確認してください。",
            "ThrottlingException" =>
                "Bedrockの利用上限に達しました。少し待ってから再実行してください。",
            "ValidationException" =>
                "Bedrockへのリクエストが受理されませんでした。モデルIDと設定を確認してください。",
            _ => $"Bedrockの呼び出しに失敗しました（{exception.ErrorCode ?? "AWSエラー"}）。"
        };
}

public sealed record BedrockChatResult(
    bool IsSuccess,
    string? Answer,
    string? ErrorMessage,
    string? ModelId,
    int? InputTokens,
    int? OutputTokens)
{
    public static BedrockChatResult Failure(string message) =>
        new(false, null, message, null, null, null);
}
