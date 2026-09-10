using System.Text.Json;
using System.Text.RegularExpressions;
using WeeklyReports;

var rootIndex = Array.IndexOf(args, "--root");
var root = Path.GetFullPath(rootIndex >= 0 ? args[rootIndex + 1] : Directory.GetCurrentDirectory());
var command = args.FirstOrDefault() ?? "run";
try
{
    if (command is "trello-check" or "trello-sync")
    {
        var settingsPath = Path.Combine(root, "config/trello.local.json");
        var settings = JsonSerializer.Deserialize<TrelloSettings>(File.ReadAllText(settingsPath), Rules.Json)
            ?? throw new ArgumentException("Trello 設定不可為空。");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var reader = new TrelloReader(client, settings);
        var board = await reader.ReadBoard(CancellationToken.None);
        if (settings.BoardId.Length == 0)
        {
            // Preserve the existing key casing and additional local settings.
            var local = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!;
            local["boardId"] = board.GetProperty("id").GetString();
            Rules.AtomicWrite(settingsPath, local.ToJsonString(Rules.Json));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(settingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            settings.BoardId = board.GetProperty("id").GetString()!;
        }
        if (command == "trello-check")
            Console.WriteLine($"Trello 連線成功：{settings.BoardName}（{settings.BoardId}），已固定看板 ID。");
        else
        {
            var snapshot = JsonSerializer.SerializeToElement(await reader.Snapshot(CancellationToken.None), Rules.Json);
            var directory = Path.Combine(root, "data/trello", settings.BoardId);
            var json = snapshot.GetRawText();
            Rules.AtomicWrite(Path.Combine(directory, "runs", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json"), json);
            Rules.AtomicWrite(Path.Combine(directory, "latest.json"), json);
            Console.WriteLine($"已保存 {settings.BoardName}：{snapshot.GetProperty("Lists").GetArrayLength()} 個清單、{snapshot.GetProperty("Cards").GetArrayLength()} 張未封存卡片。檔案：{Path.Combine(directory, "latest.json")}");
        }
        return;
    }
    if (command == "bind")
    {
        if (args.Length < 3 || !long.TryParse(args[2], out var id) || id <= 0) throw new ArgumentException("用法：bind member-1 Telegram數字ID");
        var members = Rules.Members(root);
        var member = members.SingleOrDefault(m => m.Key == args[1]) ?? throw new ArgumentException("找不到組員 key。");
        if (members.Any(m => m.Key != member.Key && m.TelegramId == id)) throw new ArgumentException("此 ID 已綁定其他組員。");
        members[members.IndexOf(member)] = member with { TelegramId = id };
        Rules.AtomicWrite(Path.Combine(root, "config/members.local.json"), JsonSerializer.Serialize(members, Rules.Json));
        Console.WriteLine($"已綁定 {member.Name}，服務下次收件即生效。");
        return;
    }
    using var store = new Store(root);
    if (command == "snapshot")
    {
        var dateIndex = Array.IndexOf(args, "--date");
        var friday = dateIndex >= 0 ? Rules.ParseFriday(args[dateIndex + 1]) : Rules.Friday(DateTimeOffset.UtcNow);
        store.Export(friday.ToString("yyyy-MM-dd"));
        Console.WriteLine(JsonSerializer.Serialize(store.Snapshot(friday, args.Contains("--include-late"), DateTimeOffset.UtcNow), Rules.Json));
        return;
    }
    if (command != "run") throw new ArgumentException("可用指令：run、bind、snapshot、trello-check、trello-sync。");
    var tokenPath = Path.Combine(root, "config/bot-token.txt");
    var token = (Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? (File.Exists(tokenPath) ? File.ReadAllText(tokenPath) : "")).Trim();
    if (!Regex.IsMatch(token, @"^\d+:[A-Za-z0-9_-]+$")) throw new ArgumentException("請將 Bot Token 放在 config/bot-token.txt（單行），或設定 TELEGRAM_BOT_TOKEN。");
    _ = Rules.Members(root);
    using var singleton = new FileStream(Path.Combine(root, "data/receiver.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    var bot = new TelegramReceiver(http, token, root, store);
    await bot.Run(stop.Token);
}
catch (OperationCanceledException) { }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
catch (Exception ex) { Console.Error.WriteLine($"執行失敗（{ex.GetType().Name}），請檢查本地設定、資料權限或服務是否重複啟動。為避免洩漏 Token，不輸出完整例外。"); Environment.ExitCode = 1; }
