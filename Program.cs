using Amazon;
using Amazon.BedrockRuntime;
using OMMS.Components;
using OMMS.Db.dao;
using OMMS.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. DIコンテナへのサービス登録
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ProductDao の登録
builder.Services.AddTransient<ProductDao>();
builder.Services.AddTransient<UserDao>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTransient<GeminiConversationDao>();
builder.Services.AddHttpClient<AIService>();
builder.Services.AddHttpClient<GeminiChatService>();
builder.Services.AddHttpClient<GeminiRagService>();
builder.Services.AddSingleton<IAmazonBedrockRuntime>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var regionName = configuration["Bedrock:Region"] ?? "ap-northeast-1";
    return new AmazonBedrockRuntimeClient(RegionEndpoint.GetBySystemName(regionName));
});
builder.Services.AddTransient<BedrockChatService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var userDao = scope.ServiceProvider.GetRequiredService<UserDao>();
    await userDao.EnsureTableAsync();
    var geminiConversationDao = scope.ServiceProvider.GetRequiredService<GeminiConversationDao>();
    await geminiConversationDao.EnsureTablesAsync();
    await geminiConversationDao.DeleteExpiredAsync();
}

// 2. HTTPリクエストパイプラインの設定
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
