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
builder.Services.AddHttpClient<AIService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var userDao = scope.ServiceProvider.GetRequiredService<UserDao>();
    await userDao.EnsureTableAsync();
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
