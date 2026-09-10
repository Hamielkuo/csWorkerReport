using System.Text.Json;
using WeeklyReports;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine($"PASS {name}"); passed++; }
void Invalid(Action action, string name) { try { action(); } catch (ArgumentException) { Check(true, name); return; } throw new Exception(name); }
Check(Rules.Friday(DateTimeOffset.Parse("2026-09-06T16:00:00Z")) == new DateOnly(2026,9,11), "台北週一跨 UTC 日期");
Check(Rules.Friday(DateTimeOffset.Parse("2026-09-13T23:59:59+08:00")) == new DateOnly(2026,9,11), "週日補交屬同週");
Check(Rules.Friday(DateTimeOffset.Parse("2026-12-31T12:00:00+08:00")) == new DateOnly(2027,1,1), "跨年週五");
Check(Rules.Parse(Rules.Template).Length == 4, "完整格式");
Invalid(() => Rules.Parse("1.進行中\n無"), "缺漏區塊");
Invalid(() => Rules.Parse(Rules.Template.Replace("1.進行中\n無", "1.進行中\n")), "空白內容");
Invalid(() => Rules.Parse(Rules.Template.Replace("2.已完成", "2.未完成")), "錯誤標題");
Invalid(() => Rules.ParseFriday("2026-09-10"), "拒絕非週五");
var root = Path.Combine(Path.GetTempPath(), "weekly-reports-test-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(Path.Combine(root, "config"));
    File.WriteAllText(Path.Combine(root,"config/members.local.json"), JsonSerializer.Serialize(new[]{new Member("a","組員甲",1),new Member("b","組員乙",null)}));
    var friday = new DateOnly(2026,9,11);
    var cutoff = Rules.Cutoff(friday);
    using (var store = new Store(root))
    {
        var report = new Report(100,"a","2026-09-11",cutoff.AddMinutes(-2),cutoff.AddMinutes(-1),Rules.Template,Rules.Parse(Rules.Template),false);
        store.Save(report);
        store.Save(report with { ReceivedAt = cutoff.AddMinutes(2), Late = true });
        Check(store.Read("2026-09-11").Count == 1 && !store.Read("2026-09-11")[0].Late, "重送不覆寫原版本");
        store.Save(report with {UpdateId=101, SubmittedAt=cutoff.AddMinutes(1), ReceivedAt=cutoff.AddMinutes(1),Late=true});
        var snap = JsonSerializer.SerializeToElement(store.Snapshot(friday,false,cutoff.AddMinutes(5)));
        Check(snap.GetProperty("Members")[0].GetProperty("Report").GetProperty("UpdateId").GetInt64()==100,"晚交不覆蓋截止前版本");
        Check(snap.GetProperty("Members")[1].GetProperty("Report").ValueKind==JsonValueKind.Null,"未交組員保留");
        var revised = JsonSerializer.SerializeToElement(store.Snapshot(friday,true,cutoff.AddMinutes(5)));
        Check(revised.GetProperty("Members")[0].GetProperty("Report").GetProperty("UpdateId").GetInt64()==101,"手動納入晚交");
        Check(Directory.GetFiles(Path.Combine(root,"data/reports/2026-09-11/members/a")).Length==2,"每版原文匯出");
        store.Offset=102;
    }
    using(var reopened = new Store(root)) Check(reopened.Offset==102 && reopened.Read("2026-09-11").Count==2,"重啟保留游標與資料");
    using(var mockStore = new Store(root))
    using(var stop = new CancellationTokenSource())
    using(var handler = new FakeTelegram(stop))
    using(var http = new HttpClient(handler))
    {
        await new TelegramReceiver(http,"123:fake",root,mockStore).Run(stop.Token);
        var rows = mockStore.Read(Rules.Friday(DateTimeOffset.UtcNow).ToString("yyyy-MM-dd"));
        Check(rows.Count(r => r.UpdateId >= 201)==1 && rows.Any(r => r.UpdateId==205),"模擬 Telegram：僅允許綁定者的完整私訊入庫");
        Check(handler.Replies.Count==5 && handler.Replies[0].Contains("999") && handler.Replies[1].Contains("未保存"),"模擬 Telegram：ID 指引與未綁定回覆");
        Check(mockStore.Offset==207,"模擬 Telegram：忽略群組後仍正確推進游標");
    }
}
finally { Directory.Delete(root,true); }
await TrelloTests.Run(Check);
ReportTests.Run(Check);
await PaginationTests.Run(Check);
Console.WriteLine($"{passed} checks passed.");
