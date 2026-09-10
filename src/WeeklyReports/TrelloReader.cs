using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeeklyReports;

public sealed class TrelloSettings
{
    public string ApiKey { get; set; } = "";
    public string Token { get; set; } = "";
    public string BoardName { get; set; } = "";
    public string BoardId { get; set; } = "";
    public string BoardShortLink { get; set; } = "";
    public void Validate()
    {
        if (!Regex.IsMatch(ApiKey, "^[A-Za-z0-9_-]+$") || !Regex.IsMatch(Token, "^[A-Za-z0-9_-]+$"))
            throw new ArgumentException("請在 config/trello.local.json 填入有效的 apiKey 與 token。");
        if (!Regex.IsMatch(BoardShortLink, "^[A-Za-z0-9]{8}$") || string.IsNullOrWhiteSpace(BoardName)
            || (BoardId.Length > 0 && !Regex.IsMatch(BoardId, "^[a-fA-F0-9]{24}$")))
            throw new ArgumentException("Trello 看板設定無效，請設定指定看板的名稱、shortLink 與 ID。");
    }
}

// Only board-scoped GET endpoints are exposed; credentials never appear in URLs or output.
public sealed class TrelloReader(HttpClient http, TrelloSettings settings)
{
    private async Task<JsonElement> Get(string suffix, CancellationToken ct)
    {
        settings.Validate();
        var board = settings.BoardId.Length > 0 ? settings.BoardId : settings.BoardShortLink;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.trello.com/1/boards/{board}{suffix}");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", $"oauth_consumer_key=\"{settings.ApiKey}\", oauth_token=\"{settings.Token}\"");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ArgumentException($"Trello 讀取失敗（HTTP {(int)response.StatusCode}）。請確認憑證、看板權限或稍後重試；未使用舊快照作為本次結果。");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }
    public async Task<JsonElement> ReadBoard(CancellationToken ct)
    {
        var board = await Get("?fields=name,shortLink,url,closed", ct);
        if (board.GetProperty("shortLink").GetString() != settings.BoardShortLink
            || board.GetProperty("name").GetString() != settings.BoardName
            || (settings.BoardId.Length > 0 && board.GetProperty("id").GetString() != settings.BoardId)
            || !Regex.IsMatch(board.GetProperty("id").GetString() ?? "", "^[a-fA-F0-9]{24}$"))
            throw new ArgumentException("Trello 回傳看板與指定看板不符，已停止讀取。");
        return board;
    }
    public async Task<object> Snapshot(CancellationToken ct)
    {
        var board = await ReadBoard(ct);
        var id = board.GetProperty("id").GetString();
        var lists = await Get("/lists?filter=all&fields=name,closed,idBoard", ct);
        var cards = await Get("/cards?filter=open&fields=name,idBoard,idList,shortUrl,due,dueComplete,closed,dateLastActivity", ct);
        if (lists.EnumerateArray().Any(l => l.GetProperty("idBoard").GetString() != id)
            || cards.EnumerateArray().Any(c => c.GetProperty("idBoard").GetString() != id))
            throw new ArgumentException("Trello 回傳其他看板的資料，已拒絕保存。");
        return new { FetchedAt = DateTimeOffset.UtcNow, CardFilter = "open", Board = board, Lists = lists, Cards = cards };
    }
}
