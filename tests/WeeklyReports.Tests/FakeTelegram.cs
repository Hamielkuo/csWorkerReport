using System.Net;
using System.Text;
using System.Text.Json;
using WeeklyReports;

public sealed class FakeTelegram(CancellationTokenSource stop) : HttpMessageHandler
{
    public List<string> Replies { get; } = [];
    private int polls;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.RequestUri!.Segments[^1];
        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
        object result;
        if (method == "getMe") result = new { id = 99 };
        else if (method == "getWebhookInfo") result = new { url = "" };
        else if (method == "getUpdates")
        {
            if (polls++ > 0) { stop.Cancel(); throw new OperationCanceledException(cancellationToken); }
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            object Update(long number, long from, string text, string type = "private") => new {
                update_id=number, message = new { date=now, from=new { id=from, is_bot=false }, chat=new { id=from, type }, text }
            };
            result = new[] {
                Update(201,999,"/id"), Update(202,999,Rules.Template),
                Update(203,1,"incomplete"), Update(204,1,Rules.Template,"group"),
                Update(205,1,Rules.Template), Update(206,1,"/status")
            };
        }
        else if (method == "sendMessage") { Replies.Add(body.GetProperty("text").GetString()!); result = new { message_id=1 }; }
        else throw new Exception("Unexpected API method");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok=true, result }), Encoding.UTF8,"application/json") };
    }
}
