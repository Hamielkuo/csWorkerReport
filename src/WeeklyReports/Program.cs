using System.Text.Json;
using WeeklyReports;

var rootIndex = Array.IndexOf(args, "--root");
var root = Path.GetFullPath(rootIndex >= 0 ? args[rootIndex + 1] : Directory.GetCurrentDirectory());
var command = args.FirstOrDefault() ?? "run";
try
{
    if (command == "run") throw new ArgumentException("Telegram Bot 收件已停用；週報改用 trello-report。歷史資料仍保留。");
    if (command == "notion-check")
    {
        var notionSettings = NotionSettings.Load(root);
        using var notionHandler = new HttpClientHandler { AllowAutoRedirect = false };
        using var notionHttp = new HttpClient(notionHandler) { Timeout = TimeSpan.FromSeconds(30) };
        var check = await new NotionClient(notionHttp, notionSettings).CheckAsync(CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(check, Rules.Json));
        return;
    }
    if (command == "trello-report")
    {
        var dateIndex = Array.IndexOf(args, "--date");
        var friday = dateIndex >= 0 ? Rules.ParseFriday(args[dateIndex + 1]) : Rules.Friday(DateTimeOffset.UtcNow);
        var preview = args.Contains("--preview");
        var forceEarly = args.Contains("--force-early");
        var refresh = args.Contains("--refresh");
        var mapping = JsonSerializer.Deserialize<ReportSettings>(File.ReadAllText(Path.Combine(root,"config/trello-report.local.json")),Rules.Json)!;
        mapping.Validate();
        var reuseIndex = Array.IndexOf(args,"--source");
        ReportSource source;
        if(reuseIndex >= 0)
            source=JsonSerializer.Deserialize<ReportSource>(File.ReadAllText(args[reuseIndex+1]),Rules.Json)!;
        else
        {
            var config=JsonSerializer.Deserialize<TrelloSettings>(File.ReadAllText(Path.Combine(root,"config/trello.local.json")),Rules.Json)!;
            if(config.BoardId!=mapping.BoardId) throw new ArgumentException("Trello 憑證與週報看板 ID 不一致。");
            using var handler=new HttpClientHandler { AllowAutoRedirect=false };
            using var client=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(60) };
            source=await new TrelloReader(client,config).ReportSnapshot(friday,CancellationToken.None);
        }
        var report=TrelloReport.Classify(source,mapping,friday,preview,forceEarly,refresh);
        var sourceRun=TrelloReport.SaveSource(root,source,report);
        var rendered = TrelloReport.Render(report);
        if (preview)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                report.Friday, report.Preview, report.ForcedEarly, Count = report.Rows.Count, SourceRun = sourceRun,
                report.ManualRefresh,
                Categories = report.Rows.GroupBy(r => r.Category).ToDictionary(g => g.Key, g => g.Count()),
                DutyCount = report.Rows.Count(r => r.Duty), Report = rendered
            }, Rules.Json));
            return;
        }
        var notionSettings = NotionSettings.Load(root);
        using var notionHandler = new HttpClientHandler { AllowAutoRedirect = false };
        using var notionHttp = new HttpClient(notionHandler) { Timeout = TimeSpan.FromSeconds(60) };
        var notion = await new NotionClient(notionHttp, notionSettings).SyncAsync(report, rendered, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            report.Friday, report.Preview, report.ForcedEarly, report.ManualRefresh, Count = report.Rows.Count, SourceRun = sourceRun,
            Categories = report.Rows.GroupBy(r => r.Category).ToDictionary(g => g.Key, g => g.Count()),
            DutyCount = report.Rows.Count(r => r.Duty), NotionRecordPageId = notion.RecordPageId,
            NotionMemberStatisticsRows = notion.StatisticsRows
        }, Rules.Json));
        return;
    }
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
    throw new ArgumentException("可用指令：trello-report、notion-check、trello-check、trello-sync；歷史查詢：snapshot、bind。");
}
catch (OperationCanceledException) { }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
catch (Exception ex) { Console.Error.WriteLine($"執行失敗（{ex.GetType().Name}），請檢查本地設定、資料權限或服務是否重複啟動。為避免洩漏 Token，不輸出完整例外。"); Environment.ExitCode = 1; }
