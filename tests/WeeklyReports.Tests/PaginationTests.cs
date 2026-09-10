using System.Net;
using System.Text;
using System.Text.Json;
using WeeklyReports;

public static class PaginationTests
{
    public static async Task Run(Action<bool,string> check)
    {
        using var handler=new PagesHandler();
        using var http=new HttpClient(handler);
        var config=new TrelloSettings {ApiKey="key",Token="token",BoardName="test",BoardShortLink="abcdefgh",BoardId="012345678901234567890123"};
        var reader=new TrelloReader(http,config);
        var source=await reader.ReportSnapshot(new DateOnly(2026,9,11),CancellationToken.None);
        check(source.Cards.Count==1001 && source.Actions.Count==1001,"Trello 卡片與動作均取得第二頁，不截斷 1000 筆");
        check(handler.Cursors==2,"Trello 分頁使用前頁末筆 ID cursor");
        handler.Repeat=true;
        try{await reader.ReportSnapshot(new DateOnly(2026,9,11),CancellationToken.None);throw new Exception("Should reject repeated pagination");}
        catch(ArgumentException){check(true,"Trello 重複分頁失敗而非產生不完整週報");}
    }
    private sealed class PagesHandler:HttpMessageHandler
    {
        public int Cursors;
        public bool Repeat;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var uri=request.RequestUri!;
            object data;
            if(uri.AbsolutePath.EndsWith("/lists")||uri.AbsolutePath.EndsWith("/members")) data=Array.Empty<object>();
            else if(uri.AbsolutePath.EndsWith("/cards")||uri.AbsolutePath.EndsWith("/actions"))
            {
                var second=uri.Query.Contains("before=");
                if(second){if(!uri.Query.Contains("before=0999"))throw new Exception("Wrong cursor");Cursors++;}
                var start=second?1000:0;
                data=Enumerable.Range(start,second?1:1000).Select(i=>new{id=Repeat&&second?"0000":i.ToString("0000"),idBoard="012345678901234567890123",idList="list",name="card",shortUrl="https://trello.com/c/test",idMembers=Array.Empty<string>(),labels=Array.Empty<object>(),closed=false,dateLastActivity="2026-09-01T00:00:00Z",due=(string?)null,type="updateCard",date="2026-09-07T00:00:00Z"}).ToArray();
            }
            else data=new{id="012345678901234567890123",name="test",shortLink="abcdefgh"};
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(data),Encoding.UTF8,"application/json")});
        }
    }
}
