using System.Text.Json;
using WeeklyReports;

public static class ReportTests
{
    public static void Run(Action<bool,string> check)
    {
        var friday=new DateOnly(2026,9,11);
        var end=Rules.Cutoff(friday); var start=end.AddDays(-7);
        var map=new ReportSettings { BoardId="board", Members=[new("a","aa","甲"),new("b","bb","乙"),new("c","cc","丙")], Lists=[new("done","處理完畢","completed"),new("todo","To Do","todo"),new("wip","Work in process","inProgress"),new("qa","測試","testing"),new("release","發布","releasing")] };
        JsonElement J(object o)=>JsonSerializer.SerializeToElement(o);
        BoardCard Card(string id,string list,string[]? people=null,CardLabel[]? labels=null)=>new(id,"board",list,id,"https://trello.com/c/test",people??["a"],labels??[],false,start.AddDays(1),null);
        JsonElement Move(string id,string card,DateTimeOffset time)=>J(new { id,type="updateCard",date=time,data=new { card=new {id=card},listBefore=new {id="wip"},listAfter=new {id="done"} } });
        var source=new ReportSource(end.AddSeconds(1),end.AddSeconds(2),J(new{id="board"}),J(map.Lists.Select(l=>new{id=l.Id,name=l.Name,closed=false})),J(map.Members.Select(m=>new{id=m.Id})),
            [Card("old","done"),Card("new","done"),Card("boundary","done"),Card("end","done"),Card("reopened","qa"),Card("backlog","todo"),Card("plan","plan"),Card("outsider","wip",["z"]),Card("shared","release",["a","b"]),Card("duty","wip",labels:[new("x","值班"),new("y","REPP")])],
            [Move("1","old",start.AddSeconds(-1)),Move("2","new",start.AddDays(1)),Move("3","boundary",start),Move("4","end",end),Move("5","reopened",start.AddDays(1))]);
        var archivedSource=source with {Cards=source.Cards.Select(c=>c with {Closed=true}).ToList()};
        check(TrelloReport.Classify(archivedSource,map,friday,false).Rows.Count==0,"已封存卡片排除所有分類，含完成與值班");
        var expanded = TrelloReport.Classify(source with {Cards=[..source.Cards,Card("unassigned","todo",[]),Card("otherTodo","todo",["z"]),Card("unassignedWip","wip",[])]},map,friday,false);
        check(expanded.Rows.Single(r=>r.CardId=="unassigned").People.SequenceEqual(new[]{"尚未安排"}) && !expanded.Rows.Any(r=>r.CardId is "otherTodo" or "unassignedWip"),"僅無人員 To Do 例外納入，顯示尚未安排");
        check(TrelloReport.Render(expanded).Contains("尚未安排｜未標示系統｜unassigned") && !TrelloReport.Render(expanded).Contains("｜未完成｜"),"未完成區塊省略重複狀態欄");
        var report=TrelloReport.Classify(source,map,friday,false);
        check(report.Rows.Where(r=>r.Category=="completed").Select(r=>r.CardId).Order().SequenceEqual(new[]{"end","new"}),"本期完成採左開右閉區間，排除舊完成");
        check(report.Rows.Single(r=>r.CardId=="reopened").Category=="testing","完成後退回測試不列已完成");
        check(report.Rows.Any(r=>r.CardId=="backlog")&&!report.Rows.Any(r=>r.CardId is "plan" or "outsider"),"To Do 全列、Plan 與未指派人員排除");
        check(report.Rows.Count(r=>r.CardId=="shared")==1 && report.Rows.Single(r=>r.CardId=="shared").People.Length==2,"多人卡片合併並列姓名");
        var duty=report.Rows.Single(r=>r.CardId=="duty");
        check(duty.Duty && duty.Systems.SequenceEqual(new[]{"REPP"}),"值班標籤不當系統名稱");
        var text=TrelloReport.Render(report);
        check(!text.Contains("本週重點") && !text.Contains("待處理") && text.Contains("三、未完成") && report.Rows.Where(r=>r.Category=="todo").All(r=>r.Stage=="未完成"),"固定四區塊，未完成只對應 To Do");
        check(text.Contains("【C# 組員週報｜") && !text.Contains("來源：") && !text.Contains("https://trello.com") && text.Contains("1. 甲、乙｜待發布／發布中｜未標示系統｜shared") && !text.Contains("  狀態："),"週報標題與單行格式，不顯示來源及連結");
        check(text.Split("｜duty").Length==2 && text.Contains("四、值班"),"值班事項僅出現一次");
        check(TrelloReport.Render(report with { Rows=report.Rows.Where(r=>!r.Duty).ToList() }).Contains("四、值班處理線上問題\n• 無符合條件的事項。"),"無值班事項保留區塊與無事項文字");
        void Reject(ReportSource s,string label){try{TrelloReport.Classify(s,map,friday,false);throw new Exception(label);}catch(ArgumentException){check(true,label);}}
        Reject(source with { Cards=[..source.Cards,Card("late","wip") with {DateLastActivity=end.AddSeconds(1)}]},"截止後變更拒絕冒充截止快照");
        Reject(source with {FetchStartedAt=end.AddMinutes(-1)},"提前執行須明示 preview");
        Reject(source with {Lists=J(map.Lists.Select(l=>new{id=l.Id,name="不符",closed=false}))},"精確清單名稱不符停止");
        var chunks=TrelloReport.Split(string.Concat(Enumerable.Repeat("長文字😀",2000)));
        check(chunks.Count>1 && chunks.All(c=>c.Length<=3500)&&chunks.All(c=>!char.IsHighSurrogate(c[^1])),"Telegram 超長內容分段且不切斷 surrogate");
        var root=Path.Combine(Path.GetTempPath(),"trello-report-test-"+Guid.NewGuid());
        try
        {
            var run=TrelloReport.Save(root,source,report);
            check(File.Exists(Path.Combine(run,"source.json"))&&File.Exists(Path.Combine(run,"classified.json")),"保存來源與分类依據");
            var official=File.ReadAllText(Path.Combine(root,"data/reports/2026-09-11/summary/latest-run.txt"));
            TrelloReport.Save(root,source,report with {Preview=true});
            check(File.ReadAllText(Path.Combine(root,"data/reports/2026-09-11/summary/latest-run.txt"))==official,"試跑不覆蓋正式輸出");
        }
        finally{Directory.Delete(root,true);}
    }
}
