using System.Net.Http.Json;
using System.Text.Json;

namespace WeeklyReports;

public sealed class TelegramReceiver(HttpClient http, string token, string root, Store store)
{
    private async Task<JsonElement> Call(string method, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/{method}", body, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.GetProperty("ok").GetBoolean())
        {
            var code = doc.RootElement.TryGetProperty("error_code", out var value) ? value.GetInt32() : 0;
            throw new TelegramException(code);
        }
        return doc.RootElement.GetProperty("result").Clone();
    }
    public async Task Run(CancellationToken ct)
    {
        await Call("getMe", new { }, ct);
        var webhook = await Call("getWebhookInfo", new { }, ct);
        if (!string.IsNullOrEmpty(webhook.GetProperty("url").GetString()))
            throw new ArgumentException("此 Bot 已設定 webhook，請先確認舊用途並解除 webhook 才能使用 long polling。");
        Console.WriteLine("週報收件服務已啟動。Ctrl+C 停止。");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await Call("getUpdates", new { offset = store.Offset, timeout = 30, allowed_updates = new[] { "message", "edited_message" } }, ct);
                foreach (var update in updates.EnumerateArray())
                {
                    await Handle(update, ct);
                    store.Offset = update.GetProperty("update_id").GetInt64() + 1;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (TelegramException ex) when (ex.Code is 401 or 409) { throw new ArgumentException($"Telegram 錯誤 {ex.Code}：請檢查 Token 或是否有其他收件服務使用同一 Bot。"); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{DateTimeOffset.Now:O} 收件暫時失敗（{ex.GetType().Name}），10 秒後重試。");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }
    private async Task Handle(JsonElement update, CancellationToken ct)
    {
        var edited = update.TryGetProperty("edited_message", out var message);
        if (!edited && !update.TryGetProperty("message", out message)) return;
        if (message.GetProperty("chat").GetProperty("type").GetString() != "private") return;
        if (!message.TryGetProperty("from", out var sender) || sender.GetProperty("is_bot").GetBoolean()) return;
        var id = sender.GetProperty("id").GetInt64();
        var chat = message.GetProperty("chat").GetProperty("id").GetInt64();
        var text = message.TryGetProperty("text", out var value) ? value.GetString() ?? "" : "";
        var cmd = text.Split(' ', '\n')[0].Split('@')[0];
        string reply;
        if (cmd is "/start" or "/id") reply = $"你的 Telegram ID：{id}\n請將此 ID 提供給管理者核對綁定。\n/template 取得格式；/status 查詢本週收件。週報可重新提交或編輯，每次都會留存。";
        else if (cmd == "/template") reply = Rules.Template;
        else
        {
            var member = Rules.Members(root).SingleOrDefault(m => m.TelegramId == id);
            if (member is null) reply = $"尚未綁定組員，未保存週報。請將 Telegram ID：{id} 提供給管理者。";
            else if (cmd == "/status")
            {
                var friday = Rules.Friday(DateTimeOffset.UtcNow).ToString("yyyy-MM-dd");
                var latest = store.Read(friday).LastOrDefault(r => r.MemberKey == member.Key);
                reply = latest is null ? $"{friday} 尚未提交。" : $"{friday} 已收件，最新提交：{TimeZoneInfo.ConvertTime(latest.SubmittedAt, Rules.Zone):MM/dd HH:mm}。" + (latest.Late ? "此版本為截止後更新。" : "此版本在截止前收到。");
            }
            else
            {
                try
                {
                    var sections = Rules.Parse(text);
                    var submitted = DateTimeOffset.FromUnixTimeSeconds(message.GetProperty(edited ? "edit_date" : "date").GetInt64());
                    // Edits remain attached to the original message's week.
                    var friday = Rules.Friday(DateTimeOffset.FromUnixTimeSeconds(message.GetProperty("date").GetInt64()));
                    var received = DateTimeOffset.UtcNow;
                    var late = submitted > Rules.Cutoff(friday) || received > Rules.Cutoff(friday);
                    store.Save(new(update.GetProperty("update_id").GetInt64(), member.Key, friday.ToString("yyyy-MM-dd"), submitted, received, text, sections, late));
                    reply = $"已保存 {member.Name} 的 {friday:yyyy-MM-dd} 週報。" + (late ? "\n此版本為截止後更新，需管理者重新整理才會納入。" : "\n將納入週五 16:50 的彙整。");
                }
                catch (ArgumentException ex) { reply = ex.Message; }
            }
        }
        try { await Call("sendMessage", new { chat_id = chat, text = reply }, ct); }
        catch (TelegramException ex) when (ex.Code is 400 or 403) { Console.Error.WriteLine("收件已處理，但 Telegram 拒絕回覆該使用者。"); }
    }
    private sealed class TelegramException(int code) : Exception { public int Code { get; } = code; }
}
