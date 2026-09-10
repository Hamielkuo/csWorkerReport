using System.Net;
using System.Text;
using WeeklyReports;

public static class TrelloTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var settings = new TrelloSettings { ApiKey="testKey", Token="testToken", BoardName="test", BoardShortLink="abcdefgh", BoardId="012345678901234567890123" };
        using var handler = new BoardHandler();
        using var http = new HttpClient(handler);
        var reader = new TrelloReader(http,settings);
        await reader.Snapshot(CancellationToken.None);
        check(handler.Count==3,"Trello 僅呼叫指定看板 GET 且憑證只在 Header");
        handler.WrongBoard=true;
        try { await reader.ReadBoard(CancellationToken.None); throw new Exception("Should reject wrong board"); }
        catch(ArgumentException) { check(true,"Trello 看板身份不符即停止"); }
        handler.WrongBoard=false;
        handler.ForeignCard=true;
        try { await reader.Snapshot(CancellationToken.None); throw new Exception("Should reject foreign card"); }
        catch(ArgumentException) { check(true,"Trello 拒絕其他看板卡片"); }
        handler.Error=true;
        try { await reader.ReadBoard(CancellationToken.None); throw new Exception("Should reject HTTP error"); }
        catch(ArgumentException ex) { check(!ex.Message.Contains("testToken")&&!ex.Message.Contains("secret-response"),"Trello API 錯誤不洩漏憑證或回應內容"); }
    }
    private sealed class BoardHandler : HttpMessageHandler
    {
        public int Count;
        public bool WrongBoard,ForeignCard,Error;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Count++;
            if(request.Method!=HttpMethod.Get || request.RequestUri!.Host!="api.trello.com"
                || !request.RequestUri.AbsolutePath.StartsWith("/1/boards/012345678901234567890123")
                || request.RequestUri.ToString().Contains("testToken") || request.Headers.Authorization?.Scheme!="OAuth")
                throw new Exception("Unexpected request");
            var body = request.RequestUri.AbsolutePath.EndsWith("/cards")
                ? (ForeignCard ? "[{\"idBoard\":\"another\"}]" : "[]")
                : request.RequestUri.AbsolutePath.EndsWith("/lists") ? "[]"
                : "{\"id\":\"012345678901234567890123\",\"name\":\"test\",\"shortLink\":\""+(WrongBoard?"wrongone":"abcdefgh")+"\"}";
            return Task.FromResult(new HttpResponseMessage(Error?HttpStatusCode.Unauthorized:HttpStatusCode.OK){ Content=new StringContent(Error?"secret-response":body,Encoding.UTF8,"application/json")});
        }
    }
}
