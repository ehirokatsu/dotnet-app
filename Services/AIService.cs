namespace OMMS.Services;

public class AIService
{
    public async Task<string> AnalyzeTextAsync(string inputText)
    {
        // 実際の外部AI API呼出（HttpClient等）をここに集約する
        await Task.Delay(1000); // 擬似的な通信遅延

        if (string.IsNullOrWhiteSpace(inputText))
        {
            return "解析するテキストを入力してください。";
        }

        // ダミー応答ロジック（後で本物のAI APIに差し替え）
        return $"【AI解析結果】\n入力されたテキスト: 「{inputText}」\n" +
               $"・文字数: {inputText.Length}文字\n" +
               $"・判定: 正常なプロンプトとして受け付けました。";
    }
}