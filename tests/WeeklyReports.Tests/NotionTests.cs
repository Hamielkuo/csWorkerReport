using System.Net;
using System.Text;
using System.Text.Json;
using WeeklyReports;

public static class NotionTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var settings = new NotionSettings
        {
            Token = "test-token",
            ReportsDataSourceId = "3d8f1c56-9f92-8027-8441-000bf164cdfe"
        };
        var friday = new DateOnly(2026, 9, 11);
        var report = new ClassifiedReport(
            "Trello", friday.ToString("yyyy-MM-dd"),
            Rules.Cutoff(friday).AddDays(-7), Rules.Cutoff(friday), Rules.Cutoff(friday),
            false,
            new ReportSettings
            {
                BoardId = "board",
                Members = [new("a", "甲", "甲"), new("b", "乙", "乙"), new("c", "丙", "丙")],
                Lists =
                [
                    new("todo", "To Do", "todo"),
                    new("wip", "Work in process", "inProgress"),
                    new("qa", "測試", "testing"),
                    new("release", "發布", "releasing"),
                    new("done", "處理完畢", "completed")
                ]
            },
            [
                new ReportRow("1", "BUG #1234", "", ["甲"], ["NPP"], "testing", "QA 測試中", "qa", false, false, null, null, null),
                new ReportRow("2", "需求 #1235", "", ["乙"], ["CB"], "inProgress", "處理中", "wip", false, false, null, null, null),
                new ReportRow("3", "需求 #1236", "", ["甲", "乙"], ["REPP"], "todo", "未完成", "todo", false, false, null, null, null)
            ]);

        var handler = new FakeNotionHandler();
        using var http = new HttpClient(handler);
        var result = await new NotionClient(http, settings).SyncAsync(report, "週報內容", CancellationToken.None);

        check(result.StatisticsRows == 3 && handler.PageCreates == 1, "Notion 同步建立一筆週報 page");
        check(handler.BlockAppends == 1, "Notion 同步將統計表與週報內容寫入 page");
        check(handler.Requests.All(r => r.Version == "2026-03-11" && r.Authorization == "Bearer test-token"), "Notion API 使用版本與 Bearer Header");
        check(handler.CreatedParents.All(p => p.Type == "data_source_id"), "Notion 新資料列使用 data source parent");
        check(handler.CreatedPropertyNames.Single().SetEquals([
            "週報名稱", "週次", "期間", "人數", "進行中", "已完成", "未完成", "線上問題", "總項目", "摘要", "來源", "整理狀態"
        ]), "Notion 主 table 欄位依 Trello 週報分類建立");
        using var blockDocument = JsonDocument.Parse(handler.LastBlockBody);
        var tableBlock = blockDocument.RootElement.GetProperty("children")
            .EnumerateArray().Single(block => block.GetProperty("type").GetString() == "table");
        var headerCell = tableBlock.GetProperty("table").GetProperty("children").EnumerateArray().First()
            .GetProperty("table_row").GetProperty("cells").EnumerateArray().First()
            .EnumerateArray().First().GetProperty("text").GetProperty("content").GetString();
        check(tableBlock.GetProperty("table").GetProperty("table_width").GetInt32() == 7 && headerCell == "人員", "Notion page 內含人員統計 table");
    }

    private sealed class FakeNotionHandler : HttpMessageHandler
    {
        public int PageCreates { get; private set; }
        public int BlockAppends { get; private set; }
        public List<RequestInfo> Requests { get; } = [];
        public List<ParentInfo> CreatedParents { get; } = [];
        public List<HashSet<string>> CreatedPropertyNames { get; } = [];
        public string LastBlockBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Headers.Authorization?.ToString() ?? "", request.Headers.GetValues("Notion-Version").Single(), request.Method, request.RequestUri!.AbsolutePath));
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.Contains("/query"))
                return Json(HttpStatusCode.OK, """{"object":"list","results":[],"has_more":false,"next_cursor":null}""");
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/pages")
            {
                using var doc = JsonDocument.Parse(body);
                var parent = doc.RootElement.GetProperty("parent");
                CreatedParents.Add(new(parent.GetProperty("type").GetString()!, parent.GetProperty("data_source_id").GetString()!));
                CreatedPropertyNames.Add(doc.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet());
                PageCreates++;
                return Json(HttpStatusCode.OK, "{\"object\":\"page\",\"id\":\"page-" + PageCreates + "\",\"properties\":{}}");
            }
            if (request.Method == HttpMethod.Patch && request.RequestUri!.AbsolutePath.Contains("/children"))
            {
                BlockAppends++;
                LastBlockBody = body;
                return Json(HttpStatusCode.OK, """{"object":"list","results":[]}""");
            }
            if (request.Method == HttpMethod.Patch && request.RequestUri!.AbsolutePath.StartsWith("/v1/pages/"))
                return Json(HttpStatusCode.OK, """{"object":"page","id":"updated"}""");
            throw new InvalidOperationException($"Unexpected fake Notion request: {request.Method} {request.RequestUri}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed record RequestInfo(string Authorization, string Version, HttpMethod Method, string Path);
    private sealed record ParentInfo(string Type, string DataSourceId);
}
