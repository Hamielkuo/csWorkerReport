using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace WeeklyReports;

public record ReportPerson(string Id, string Username, string Name);
public record ReportList(string Id, string Name, string Category);
public sealed class ReportSettings
{
    public string BoardId { get; set; } = "";
    public string DutyLabel { get; set; } = "Worktrack";
    public List<ReportPerson> Members { get; set; } = [];
    public List<ReportList> Lists { get; set; } = [];
    public void Validate()
    {
        var categories = new[] { "todo", "inProgress", "testing", "releasing", "completed" };
        if (Members.Count != 3 || Members.Any(m => string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Name))
            || Members.Select(m => m.Id).Distinct().Count() != Members.Count
            || Lists.Count != 5 || Lists.Select(l => l.Id).Distinct().Count() != Lists.Count
            || !Lists.Select(l => l.Category).Order().SequenceEqual(categories.Order()))
            throw new ArgumentException("週報人員或五個清單對照設定不完整／重複。");
    }
}
public record BoardCard(string Id, string IdBoard, string IdList, string Name, string ShortUrl,
    string[] IdMembers, CardLabel[] Labels, bool Closed, DateTimeOffset DateLastActivity, DateTimeOffset? Due);
public record CardLabel(string Id, string Name);
public record ReportSource(DateTimeOffset FetchStartedAt, DateTimeOffset FetchFinishedAt, JsonElement Board,
    JsonElement Lists, JsonElement Members, List<BoardCard> Cards, List<JsonElement> Actions);
public record ReportRow(string CardId, string Title, string Url, string[] People, string[] Systems,
    string Category, string Stage, string ListId, bool Duty, bool Archived, DateTimeOffset? Due,
    DateTimeOffset? CompletedAt, string? CompletionActionId)
{
    public string[] SourceCardIds { get; init; } = [];
}
public record ClassifiedReport(string Source, string Friday, DateTimeOffset PeriodStartExclusive,
    DateTimeOffset PeriodEndInclusive, DateTimeOffset AsOf, bool Preview, ReportSettings Mapping,
    List<ReportRow> Rows);

public static class TrelloReport
{
    public static ClassifiedReport Classify(ReportSource source, ReportSettings config, DateOnly friday, bool preview)
    {
        config.Validate();
        if (source.Board.GetProperty("id").GetString() != config.BoardId) throw new ArgumentException("週報看板 ID 不符。");
        foreach (var list in config.Lists)
        {
            var matches = source.Lists.EnumerateArray().Where(l => l.GetProperty("id").GetString() == list.Id).ToList();
            if (matches.Count != 1 || matches[0].GetProperty("name").GetString() != list.Name || matches[0].GetProperty("closed").GetBoolean())
                throw new ArgumentException($"週報清單「{list.Name}」名稱不符、已封存或不存在。");
        }
        foreach (var person in config.Members)
            if (!source.Members.EnumerateArray().Any(m => m.GetProperty("id").GetString() == person.Id))
                throw new ArgumentException($"週報人員「{person.Name}」不在此看板，請確認成員設定。");
        var cutoff = Rules.Cutoff(friday);
        var start = cutoff.AddDays(-7);
        if (!preview && source.FetchStartedAt < cutoff) throw new ArgumentException("尚未到週五 16:50；提前試跑請加 --preview。");
        var asOf = preview && source.FetchStartedAt < cutoff ? source.FetchStartedAt : cutoff;
        if (asOf <= start) throw new ArgumentException("試跑日期尚未進入本期。");
        // Trello is not a historical snapshot API. Refuse uncertain historical reconstruction.
        // Any board card changed after the target can have left scope (member/list/label change).
        if (source.Cards.Any(c => c.DateLastActivity > asOf) || source.Actions.Any(a => a.GetProperty("date").GetDateTimeOffset() > asOf))
            throw new ArgumentException("看板在截止／擷取開始後有變更，無法保證當時清單、人員及標籤狀態。未產生正式週報；請使用已保存的截止快照，或人工確認後另做目前狀態報告。");
        var rows = new List<ReportRow>();
        foreach (var card in source.Cards.DistinctBy(c => c.Id))
        {
            if (card.IdBoard != config.BoardId) throw new ArgumentException("拒絕其他看板的卡片。");
            if (card.Closed) continue; // Archived cards never belong to the report.
            var list = config.Lists.SingleOrDefault(l => l.Id == card.IdList);
            if (list is null) continue;
            var people = config.Members.Where(m => card.IdMembers.Contains(m.Id)).Select(m => m.Name).ToArray();
            if (people.Length == 0 && list.Category == "todo" && card.IdMembers.Length == 0) people = ["尚未安排"];
            if (people.Length == 0) continue;
            JsonElement? completion = null;
            if (list.Category == "completed")
            {
                completion = source.Actions.Where(a => EnteredList(a, card.Id, list.Id))
                    .Where(a => a.GetProperty("date").GetDateTimeOffset() > start && a.GetProperty("date").GetDateTimeOffset() <= asOf)
                    .OrderByDescending(a => a.GetProperty("date").GetDateTimeOffset()).Select(a => (JsonElement?)a).FirstOrDefault();
                if (completion is null) continue;
            }
            var labels = card.Labels.Select(l => l.Name.Trim()).Where(n => n.Length > 0).Distinct().ToArray();
            var systems = labels.Where(n => n != config.DutyLabel).ToArray();
            var stage = list.Category switch { "todo" => "未完成", "testing" => "QA 測試中", "releasing" => "待發布／發布中", "completed" => "已發布生產／問題已解決", _ => "處理中" };
            rows.Add(new(card.Id, card.Name, card.ShortUrl, people, systems.Length == 0 ? ["未標示系統"] : systems,
                list.Category, stage, list.Id, labels.Contains(config.DutyLabel), card.Closed, card.Due,
                completion?.GetProperty("date").GetDateTimeOffset(), completion?.GetProperty("id").GetString()));
        }
        return new("Trello", friday.ToString("yyyy-MM-dd"), start, cutoff, asOf, preview, config,
            Deduplicate(rows).OrderBy(r => r.Category).ThenBy(r => r.People[0], StringComparer.Ordinal).ThenBy(r => r.Title, StringComparer.Ordinal).ToList());
    }
    public static bool EnteredList(JsonElement action, string cardId, string listId)
    {
        var data = action.GetProperty("data");
        if (!data.TryGetProperty("card", out var card) || card.GetProperty("id").GetString() != cardId) return false;
        // Only explicit movement into the completed list counts as completion.
        return action.GetProperty("type").GetString() == "updateCard"
            && data.TryGetProperty("listAfter", out var after) && after.GetProperty("id").GetString() == listId
            && data.TryGetProperty("listBefore", out var before) && before.GetProperty("id").GetString() != listId;
    }
    public static string WorkKey(ReportRow row)
    {
        var match = Regex.Match(row.Title, @"(?:#\s*|(?:需求|BUG)\s*#?\s*)(\d{4,})(?!\d)|^\s*[A-Za-z][A-Za-z0-9._/-]*\s+(\d{4,})(?!\d)", RegexOptions.IgnoreCase);
        return match.Success ? "ticket:" + (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : "card:" + row.CardId;
    }
    public static List<ReportRow> Deduplicate(IEnumerable<ReportRow> input)
    {
        int Rank(string category) => category switch { "completed" => 5, "releasing" => 4, "testing" => 3, "inProgress" => 2, _ => 1 };
        return input.GroupBy(WorkKey).Select(group =>
        {
            var copies = group.ToList();
            var chosen = copies.OrderByDescending(r => Rank(r.Category)).ThenBy(r => r.CardId, StringComparer.Ordinal).First();
            var people = copies.SelectMany(r => r.People).Distinct().Order(StringComparer.Ordinal).ToArray();
            if (people.Length > 1) people = people.Where(p => p != "尚未安排").ToArray();
            return chosen with { Duty = copies.Any(r => r.Duty), People = people,
                Systems = copies.SelectMany(r => r.Systems).Distinct().ToArray(),
                SourceCardIds = copies.SelectMany(r => r.SourceCardIds.Length > 0 ? r.SourceCardIds : [r.CardId]).Distinct().ToArray() };
        }).ToList();
    }
    public static string Render(ClassifiedReport report)
    {
        var rows = Deduplicate(report.Rows);
        var active = rows.Where(r => !r.Duty && r.Category is "inProgress" or "testing" or "releasing").ToList();
        var completed = rows.Where(r => !r.Duty && r.Category == "completed").ToList();
        var todo = rows.Where(r => !r.Duty && r.Category == "todo").ToList();
        var duty = rows.Where(r => r.Duty).ToList();
        var b = new StringBuilder();
        b.AppendLine($"【C# 組員週報｜{report.Friday}】\n");
        b.AppendLine($"期間：{report.PeriodStartExclusive:MM/dd HH:mm} ～ {report.AsOf.ToOffset(TimeSpan.FromHours(8)):MM/dd HH:mm}");
        var memberCount = rows.SelectMany(r => r.People).Where(p => p != "尚未安排").Distinct().Count();
        b.AppendLine($"👨‍💻 {memberCount} 人｜進行中 {active.Count}｜已完成 {completed.Count}｜未完成 {todo.Count}｜線上問題 {duty.Count}");
        if (report.Preview) b.AppendLine("\n⚠️ 提前試跑，非正式截止報告");
        int StageOrder(string category) => category switch { "inProgress" => 0, "testing" => 1, "releasing" => 2, "completed" => 3, _ => 4 };
        void Section(string title, List<ReportRow> items, bool groupStage = false)
        {
            b.AppendLine($"\n━━━━━━━━━━\n{title}\n━━━━━━━━━━\n");
            if (items.Count == 0) { b.AppendLine("無"); return; }
            var sectionIndex = 0;
            foreach (var person in items.GroupBy(r => string.Join("、", r.People)).OrderBy(g => g.Key == "尚未安排" ? 0 : 1).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                b.AppendLine($"👤 {person.Key}｜{person.Count()} 項\n");
                var index = groupStage ? 0 : sectionIndex;
                foreach (var group in person.OrderBy(r => StageOrder(r.Category)).GroupBy(r => groupStage ? r.Stage : ""))
                {
                    if (groupStage) b.AppendLine($"【{group.Key}】");
                    if (groupStage) b.AppendLine();
                    foreach (var row in group)
                    {
                        var titleText = row.Title.Replace('\r', ' ').Replace('\n', ' ');
                        var systems = row.Systems;
                        var prefix = systems.Length == 0 ? "" : string.Join("／", systems) + "｜";
                        b.AppendLine($"{++index}. {prefix}{titleText}");
                        sectionIndex++;
                        if (row.CompletedAt is {} at) b.AppendLine($"  完成移入時間：{at.ToOffset(TimeSpan.FromHours(8)):MM/dd HH:mm}");
                        if (row.Due is {} due) b.AppendLine($"  Trello 到期日：{due.ToOffset(TimeSpan.FromHours(8)):yyyy/MM/dd HH:mm}");
                    }
                    b.AppendLine();
                }
            }
        }
        Section("一、進行中", active, true);
        Section("二、已完成", completed);
        Section("三、未完成", todo);
        Section("四、值班處理線上問題", duty, true);
        return b.ToString();
    }
    public static List<string> Split(string text, int maxLength = 3500)
    {
        var chunks = new List<string>();
        var budget = maxLength - 40;
        if(budget < 10) throw new ArgumentException("分段長度過小。");
        while(text.Length > budget)
        {
            var end = text.LastIndexOf('\n',budget);
            if(end <= 0) end = budget;
            if(char.IsHighSurrogate(text[end-1])) end--;
            chunks.Add(text[..end]); text=text[end..].TrimStart('\n');
        }
        if(text.Length>0) chunks.Add(text);
        return chunks.Count==1 ? chunks : chunks.Select((s,i)=>$"第 {i+1}/{chunks.Count} 則\n{s}").ToList();
    }
    public static string Save(string root, ReportSource source, ClassifiedReport report)
    {
        var summary = Path.Combine(root,"data/reports",report.Friday,"summary");
        var run = Path.Combine(summary,"runs",DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd-HHmmss-fffffff")+"-trello");
        var text = Render(report);
        Rules.AtomicWrite(Path.Combine(run,"source.json"),JsonSerializer.Serialize(source,Rules.Json));
        Rules.AtomicWrite(Path.Combine(run,"classified.json"),JsonSerializer.Serialize(report,Rules.Json));
        Rules.AtomicWrite(Path.Combine(run,"telegram.txt"),text);
        Rules.AtomicWrite(Path.Combine(run,"report.md"),$"# C# 組員週報\n\n產生時間：{source.FetchFinishedAt:O}\n\n正式截止：{report.PeriodEndInclusive:O}\n\n試跑：{report.Preview}\n\n"+text);
        var chunks=Split(text);
        for(var i=0;i<chunks.Count;i++) Rules.AtomicWrite(Path.Combine(run,$"telegram-{i+1:00}.txt"),chunks[i]);
        // A preview never overwrites the formal report pointer/files.
        var prefix = report.Preview ? "preview-" : "";
        foreach(var file in new[]{"report.md","telegram.txt"}) Rules.AtomicWrite(Path.Combine(summary,prefix+file),File.ReadAllText(Path.Combine(run,file)));
        Rules.AtomicWrite(Path.Combine(summary,prefix+"latest-run.txt"),run);
        return run;
    }
}
