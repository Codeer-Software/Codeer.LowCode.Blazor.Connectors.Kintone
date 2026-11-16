using Kintone.Mock;
using System.Text.Json;

//kintone REST API のモック。
//  dotnet run --project Source/Kintone.Mock [-- --urls http://localhost:5084] [--data <mock-data.json>]
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://localhost:5084");

var dataPath = builder.Configuration["data"];
dataPath = string.IsNullOrEmpty(dataPath) ? Path.Combine(AppContext.BaseDirectory, "mock-data.json") : Path.GetFullPath(dataPath);
MockKintone mock;
try
{
    mock = MockKintone.FromFile(dataPath);
}
catch (Exception e) when (e is InvalidDataException || e is JsonException || e is IOException)
{
    Console.Error.WriteLine($"データのファイルを読めません ({dataPath}): {e.Message}");
    return 1;
}

var app = builder.Build();
app.MapMockKintone(mock);

app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = app.Urls.FirstOrDefault(e => e.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? app.Urls.FirstOrDefault() ?? "http://localhost:5084";
    var uri = new UriBuilder(url);
    if (uri.Host is "0.0.0.0" or "[::]" or "::" or "+" or "*") uri.Host = "localhost";
    var host = uri.Uri.GetLeftPart(UriPartial.Authority);

    Console.WriteLine();
    Console.WriteLine($"kintone (REST API) のモックを起動しました: {host}{MockKintone.BasePath}/v1/");
    Console.WriteLine("データ: " + dataPath);
    Console.WriteLine();
    Console.WriteLine("アプリ:");
    foreach (var a in mock.Apps)
    {
        var path = a.GuestSpaceId == null ? "/k/v1/" : $"/k/guest/{a.GuestSpaceId}/v1/";
        Console.WriteLine($"  {a.Id} {a.Name}{(a.Code.Length > 0 ? $" (アプリコード {a.Code})" : "")} {path}");
        foreach (var t in a.Tokens) Console.WriteLine($"      API トークン {t.Token} ({t.Permission})");
    }
    Console.WriteLine("利用者 (パスワード認証):");
    foreach (var u in mock.Users) Console.WriteLine($"  {u.Code} / {u.Password} ({u.Name})");
    Console.WriteLine();
    Console.WriteLine("データはこのプロセスの中だけにあります。止めると初期データに戻ります。終了は Ctrl+C。");
    Console.WriteLine();
});

app.Run();
return 0;

/// <summary>テストから WebApplicationFactory などで立てるための宣言。</summary>
public partial class Program { }
