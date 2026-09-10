using System.Text.Json;
namespace WeeklyReports;

public sealed partial class TrelloReader
{
    // Follow ID cursors, not page offsets, so new actions cannot shift old pages.
    internal async Task<List<JsonElement>> Pages(string suffix, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        var ids = new HashSet<string>();
        string? before = null;
        for(var page=0;page<500;page++)
        {
            var values = await Get(suffix + "&limit=1000" + (before is null ? "" : "&before="+Uri.EscapeDataString(before)),ct);
            var batch = values.EnumerateArray().ToList();
            if(batch.Count==0) return result;
            foreach(var value in batch)
            {
                var id=value.GetProperty("id").GetString()!;
                if(!ids.Add(id)) throw new ArgumentException("Trello 分頁重複，拒絕可能不完整的週報資料。");
                result.Add(value);
            }
            if(batch.Count<1000) return result;
            before=batch[^1].GetProperty("id").GetString();
        }
        throw new ArgumentException("Trello 資料超過安全分頁上限，尚未取得完整資料，已停止。");
    }
    public async Task<ReportSource> ReportSnapshot(DateOnly friday, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var board = await ReadBoard(ct);
        var lists = await Get("/lists?filter=all&fields=name,closed,idBoard",ct);
        var members = await Get("/members?fields=username,fullName",ct);
        var cards = await Pages("/cards?filter=all&sort=-id&fields=name,idBoard,idList,shortUrl,idMembers,labels,closed,dateLastActivity,due",ct);
        var since = Rules.Cutoff(friday).AddDays(-7).AddSeconds(-1).ToUniversalTime().ToString("O");
        var actions = await Pages("/actions?filter=all&fields=id,type,date,data&memberCreator=false&member=false&since="+Uri.EscapeDataString(since),ct);
        var id=board.GetProperty("id").GetString();
        if(lists.EnumerateArray().Any(l=>l.GetProperty("idBoard").GetString()!=id)) throw new ArgumentException("週報清單屬於其他看板。");
        var parsed=cards.Select(c=>c.Deserialize<BoardCard>(Rules.Json)!).ToList();
        if(parsed.Any(c=>c.IdBoard!=id)) throw new ArgumentException("週報卡片屬於其他看板。");
        return new(started,DateTimeOffset.UtcNow,board,lists,members,parsed,actions);
    }
}
