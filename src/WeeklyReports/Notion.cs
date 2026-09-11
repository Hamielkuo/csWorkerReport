using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeeklyReports;

public sealed class NotionSettings
{
    public string Token { get; set; } = "";
    public string ApiVersion { get; set; } = "2026-03-11";
    public string ReportsDataSourceId { get; set; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Token))
            throw new ArgumentException("請在 config/notion.local.json 填入 Notion token。");
        if (!Guid.TryParse(ReportsDataSourceId, out _))
            throw new ArgumentException("請在 config/notion.local.json 填入有效的 Notion 週報 data source ID。");
        if (!string.Equals(ApiVersion, "2026-03-11", StringComparison.Ordinal))
            throw new ArgumentException("Notion API 版本必須使用 2026-03-11。");
    }

    public static NotionSettings Load(string root)
    {
        var path = Path.Combine(root, "config/notion.local.json");
        if (!File.Exists(path)) throw new ArgumentException("找不到 config/notion.local.json。");
        var settings = JsonSerializer.Deserialize<NotionSettings>(File.ReadAllText(path), Rules.Json)
            ?? throw new ArgumentException("Notion 設定不可為空。");
        settings.Validate();
        return settings;
    }
}

public sealed record WeeklySummary(
    DateOnly Friday,
    int MemberCount,
    int InProgress,
    int Completed,
    int Todo,
    int Duty,
    int Total);

public sealed record WeeklyMemberStatistic(
    string Person,
    DateOnly Friday,
    int InProgress,
    int Completed,
    int Todo,
    int Duty,
    int Total,
    string WorkFocus);

public static class WeeklyStatistics
{
    public static WeeklySummary BuildSummary(ClassifiedReport report)
    {
        var rows = FinalRows(report);
        var inProgress = rows.Count(IsInProgress);
        var completed = rows.Count(r => !r.Duty && r.Category == "completed");
        var todo = rows.Count(r => !r.Duty && r.Category == "todo");
        var duty = rows.Count(r => r.Duty);
        return new(
            DateOnly.Parse(report.Friday),
            rows.SelectMany(r => r.People).Where(p => p != "尚未安排").Distinct(StringComparer.Ordinal).Count(),
            inProgress,
            completed,
            todo,
            duty,
            inProgress + completed + todo + duty);
    }

    public static List<WeeklyMemberStatistic> Build(ClassifiedReport report)
    {
        var rows = FinalRows(report);
        var friday = DateOnly.Parse(report.Friday);
        return report.Mapping.Members.Select(member =>
        {
            var personRows = rows.Where(r => r.People.Contains(member.Name, StringComparer.Ordinal)).ToList();
            var inProgress = personRows.Count(IsInProgress);
            var completed = personRows.Count(r => !r.Duty && r.Category == "completed");
            var todo = personRows.Count(r => !r.Duty && r.Category == "todo");
            var duty = personRows.Count(r => r.Duty);
            return new WeeklyMemberStatistic(
                member.Name,
                friday,
                inProgress,
                completed,
                todo,
                duty,
                inProgress + completed + todo + duty,
                BuildWorkFocus(personRows));
        }).ToList();
    }

    private static List<ReportRow> FinalRows(ClassifiedReport report) => TrelloReport.Deduplicate(report.Rows);

    private static bool IsInProgress(ReportRow row) =>
        !row.Duty && row.Category is "inProgress" or "testing" or "releasing";

    private static string BuildWorkFocus(IEnumerable<ReportRow> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return "本週無列入事項";
        var stages = list.Select(r => r.Stage).Distinct(StringComparer.Ordinal).ToArray();
        var systems = list.SelectMany(r => r.Systems)
            .Where(s => s != "未標示系統")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var focus = $"階段：{string.Join("、", stages)}";
        return systems.Length == 0 ? focus : $"{focus}；系統：{string.Join("、", systems)}";
    }
}

public sealed class NotionClient(HttpClient http, NotionSettings settings)
{
    private const int RichTextLimit = 1800;

    public async Task<NotionCheckResult> CheckAsync(CancellationToken ct)
    {
        settings.Validate();
        var reports = await SendAsync(HttpMethod.Get, $"/v1/data_sources/{settings.ReportsDataSourceId}", null, ct);
        return new(
            DataSourceName(reports),
            reports.GetProperty("properties").EnumerateObject().Count());
    }

    public async Task<NotionSyncResult> SyncAsync(ClassifiedReport report, string renderedReport, CancellationToken ct)
    {
        settings.Validate();
        var summary = WeeklyStatistics.BuildSummary(report);
        var memberStatistics = WeeklyStatistics.Build(report);
        var reportTitle = $"C# 組員週報｜{report.Friday}{(report.ForcedEarly ? "｜提前測試" : "")}";
        var existing = await FindRowAsync(settings.ReportsDataSourceId, "週報名稱", reportTitle, summary.Friday, ct);
        var properties = ReportProperties(report, reportTitle, summary);
        string pageId;
        if (existing is null)
        {
            pageId = await CreatePageAsync(settings.ReportsDataSourceId, properties, ct);
            await AppendReportAsync(pageId, renderedReport, summary, memberStatistics, ct);
        }
        else
        {
            pageId = existing.Value.GetProperty("id").GetString()!;
            await UpdatePageAsync(pageId, properties, ct);
            await ReplaceReportAsync(pageId, renderedReport, summary, memberStatistics, ct);
        }
        return new(report.Friday, memberStatistics.Count, pageId);
    }

    private async Task<JsonElement?> FindRowAsync(string dataSourceId, string titleProperty, string title, DateOnly friday, CancellationToken ct)
    {
        string? cursor = null;
        do
        {
            var body = new JsonObject { ["page_size"] = 100 };
            if (cursor is not null) body["start_cursor"] = cursor;
            var root = await SendAsync(HttpMethod.Post, $"/v1/data_sources/{dataSourceId}/query", body, ct);
            foreach (var page in root.GetProperty("results").EnumerateArray())
            {
                if (PropertyTitle(page, titleProperty) == title && PropertyDate(page, "週次") == friday)
                    return page.Clone();
            }
            cursor = root.TryGetProperty("next_cursor", out var next) && next.ValueKind != JsonValueKind.Null
                ? next.GetString() : null;
        } while (cursor is not null);
        return null;
    }

    private async Task<string> CreatePageAsync(string dataSourceId, JsonObject properties, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["parent"] = new JsonObject { ["type"] = "data_source_id", ["data_source_id"] = dataSourceId },
            ["properties"] = properties
        };
        var root = await SendAsync(HttpMethod.Post, "/v1/pages", body, ct);
        return root.GetProperty("id").GetString()!;
    }

    private async Task UpdatePageAsync(string pageId, JsonObject properties, CancellationToken ct)
    {
        await SendAsync(HttpMethod.Patch, $"/v1/pages/{pageId}", new JsonObject { ["properties"] = properties }, ct);
    }

    private async Task ReplaceReportAsync(
        string pageId,
        string renderedReport,
        WeeklySummary summary,
        IReadOnlyList<WeeklyMemberStatistic> memberStatistics,
        CancellationToken ct)
    {
        foreach (var childId in await ChildIdsAsync(pageId, ct))
            await SendAsync(HttpMethod.Delete, $"/v1/blocks/{childId}", null, ct);
        await AppendReportAsync(pageId, renderedReport, summary, memberStatistics, ct);
    }

    private async Task<List<string>> ChildIdsAsync(string pageId, CancellationToken ct)
    {
        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var path = $"/v1/blocks/{pageId}/children?page_size=100";
            if (cursor is not null) path += "&start_cursor=" + Uri.EscapeDataString(cursor);
            var root = await SendAsync(HttpMethod.Get, path, null, ct);
            ids.AddRange(root.GetProperty("results").EnumerateArray()
                .Select(block => block.GetProperty("id").GetString()!)
                .Where(id => id.Length > 0));
            cursor = root.TryGetProperty("next_cursor", out var next) && next.ValueKind != JsonValueKind.Null
                ? next.GetString() : null;
        } while (cursor is not null);
        return ids;
    }

    private async Task AppendReportAsync(
        string pageId,
        string renderedReport,
        WeeklySummary summary,
        IReadOnlyList<WeeklyMemberStatistic> memberStatistics,
        CancellationToken ct)
    {
        var children = new JsonArray
        {
            Heading("週報統計"),
            StatisticsTable(summary, memberStatistics),
            new JsonObject { ["object"] = "block", ["type"] = "divider", ["divider"] = new JsonObject() },
            Heading("週報紀錄")
        };
        foreach (var chunk in Chunks(renderedReport, RichTextLimit))
        {
            children.Add(new JsonObject
            {
                ["object"] = "block",
                ["type"] = "paragraph",
                ["paragraph"] = new JsonObject
                {
                    ["rich_text"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = new JsonObject { ["content"] = chunk }
                    })
                }
            });
        }
        await SendAsync(HttpMethod.Patch, $"/v1/blocks/{pageId}/children", new JsonObject { ["children"] = children }, ct);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, "https://api.notion.com" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        request.Headers.Add("Notion-Version", settings.ApiVersion);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = "unknown";
            var message = "";
            try { code = JsonDocument.Parse(raw).RootElement.TryGetProperty("code", out var c) ? c.GetString() ?? code : code; }
            catch (JsonException) { }
            try { message = JsonDocument.Parse(raw).RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : ""; }
            catch (JsonException) { }
            var detail = string.IsNullOrWhiteSpace(message) ? "" : $"：{message}";
            throw new ArgumentException($"Notion API 失敗（HTTP {(int)response.StatusCode}, {code}）{detail}。請確認 integration 權限與資料庫設定。");
        }
        if (string.IsNullOrWhiteSpace(raw)) return JsonDocument.Parse("{}").RootElement.Clone();
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static JsonObject ReportProperties(ClassifiedReport report, string title, WeeklySummary summary) => new()
    {
        ["週報名稱"] = Title(title),
        ["週次"] = Date(summary.Friday),
        ["期間"] = RichText($"{report.PeriodStartExclusive:MM/dd HH:mm} ～ {report.AsOf.ToOffset(TimeSpan.FromHours(8)):MM/dd HH:mm}"),
        ["人數"] = Number(summary.MemberCount),
        ["進行中"] = Number(summary.InProgress),
        ["已完成"] = Number(summary.Completed),
        ["未完成"] = Number(summary.Todo),
        ["線上問題"] = Number(summary.Duty),
        ["總項目"] = Number(summary.Total),
        ["摘要"] = RichText($"進行中／已完成／未完成／線上問題：{summary.InProgress}／{summary.Completed}／{summary.Todo}／{summary.Duty}"),
        ["來源"] = RichText("Trello"),
        ["整理狀態"] = new JsonObject
        {
            ["select"] = new JsonObject { ["name"] = report.ForcedEarly ? "提前測試" : "已整理" }
        }
    };

    private static JsonObject StatisticsTable(WeeklySummary summary, IReadOnlyList<WeeklyMemberStatistic> members)
    {
        var rows = new JsonArray
        {
            TableRow(["人員", "進行中", "已完成", "未完成", "線上問題", "合計", "工作重點"])
        };
        foreach (var member in members)
            rows.Add(TableRow([member.Person, member.InProgress.ToString(), member.Completed.ToString(), member.Todo.ToString(), member.Duty.ToString(), member.Total.ToString(), member.WorkFocus]));
        rows.Add(TableRow(["合計", summary.InProgress.ToString(), summary.Completed.ToString(), summary.Todo.ToString(), summary.Duty.ToString(), summary.Total.ToString(), "全體週報統計"]));
        return new JsonObject
        {
            ["object"] = "block",
            ["type"] = "table",
            ["table"] = new JsonObject
            {
                ["table_width"] = 7,
                ["has_column_header"] = true,
                ["has_row_header"] = false,
                ["children"] = rows
            }
        };
    }

    private static JsonObject TableRow(IEnumerable<string> values)
    {
        var cells = new JsonArray();
        foreach (var value in values) cells.Add(TextArray(value));
        return new JsonObject
        {
            ["object"] = "block",
            ["type"] = "table_row",
            ["table_row"] = new JsonObject { ["cells"] = cells }
        };
    }

    private static JsonObject Heading(string value) => new()
    {
        ["object"] = "block",
        ["type"] = "heading_2",
        ["heading_2"] = new JsonObject { ["rich_text"] = TextArray(value) }
    };

    private static JsonObject Title(string value) => new() { ["title"] = TextArray(value) };
    private static JsonObject RichText(string value) => new() { ["rich_text"] = TextArray(value) };
    private static JsonObject Number(int value) => new() { ["number"] = value };
    private static JsonObject Date(DateOnly value) => new() { ["date"] = new JsonObject { ["start"] = value.ToString("yyyy-MM-dd") } };
    private static JsonArray TextArray(string value) => new(new JsonObject
    {
        ["type"] = "text",
        ["text"] = new JsonObject { ["content"] = value }
    });

    private static string PropertyTitle(JsonElement page, string name)
    {
        if (!page.GetProperty("properties").TryGetProperty(name, out var property)
            || !property.TryGetProperty("title", out var title)) return "";
        return string.Concat(title.EnumerateArray().Select(t => t.GetProperty("plain_text").GetString() ?? ""));
    }

    private static DateOnly? PropertyDate(JsonElement page, string name)
    {
        if (!page.GetProperty("properties").TryGetProperty(name, out var property)
            || !property.TryGetProperty("date", out var date) || date.ValueKind == JsonValueKind.Null
            || !date.TryGetProperty("start", out var start)) return null;
        return DateOnly.TryParse(start.GetString(), out var value) ? value : null;
    }

    private static IEnumerable<string> Chunks(string value, int maxLength)
    {
        for (var offset = 0; offset < value.Length; offset += maxLength)
            yield return value.Substring(offset, Math.Min(maxLength, value.Length - offset));
    }

    private static string DataSourceName(JsonElement source)
    {
        if (source.TryGetProperty("name", out var name)) return name.GetString() ?? "";
        if (!source.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(title.EnumerateArray()
            .Select(t => t.TryGetProperty("plain_text", out var text) ? text.GetString() ?? "" : ""));
    }
}

public sealed record NotionCheckResult(string ReportsDataSourceName, int ReportsPropertyCount);

public sealed record NotionSyncResult(string Friday, int StatisticsRows, string RecordPageId);
