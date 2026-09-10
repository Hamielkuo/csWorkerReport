using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeeklyReports;

public record Member(string Key, string Name, long? TelegramId);
public record Report(long UpdateId, string MemberKey, string Friday, DateTimeOffset SubmittedAt,
    DateTimeOffset ReceivedAt, string Raw, string[] Sections, bool Late);

public static class Rules
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public const string Template = "1.進行中\n無\n\n2.已完成\n無\n\n3.未完成\n無\n\n4.值班處理線上問題\n無";
    public static DateOnly Friday(DateTimeOffset at)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone).DateTime);
        var sinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(4 - sinceMonday);
    }
    public static DateTimeOffset Cutoff(DateOnly friday) => new(friday.ToDateTime(new TimeOnly(16, 50)), TimeSpan.FromHours(8));
    public static DateOnly ParseFriday(string date)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day.DayOfWeek != DayOfWeek.Friday)
            throw new ArgumentException("回報日期必須是 yyyy-MM-dd 格式的週五。");
        return day;
    }
    public static string[] Parse(string raw)
    {
        var titles = new[] { "進行中", "已完成", "未完成", "值班處理線上問題" };
        var normalized = raw.Replace("\r\n", "\n").Trim();
        var matches = Regex.Matches(normalized, @"(?m)^[ \t]*([1-4])[.．、\)][ \t]*(進行中|已完成|未完成|值班處理線上問題)[：: \t]*$");
        if (matches.Count != 4 || normalized[..matches[0].Index].Trim().Length != 0)
            throw new ArgumentException("請直接使用 /template 的四個區塊，一次提交完整週報；沒有事項請填「無」。");
        var sections = new string[4];
        for (var i = 0; i < 4; i++)
        {
            if (matches[i].Groups[1].Value != (i + 1).ToString() || matches[i].Groups[2].Value != titles[i])
                throw new ArgumentException("四個區塊的順序或標題不正確，請使用 /template。");
            var begin = matches[i].Index + matches[i].Length;
            var end = i == 3 ? normalized.Length : matches[i + 1].Index;
            sections[i] = normalized[begin..end].Trim();
            if (sections[i].Length == 0) throw new ArgumentException($"「{titles[i]}」不可空白，沒有事項請填「無」。");
        }
        return sections;
    }
    public static List<Member> Members(string root)
    {
        var members = JsonSerializer.Deserialize<List<Member>>(File.ReadAllText(Path.Combine(root, "config/members.local.json")), Json)!;
        if (members.Count == 0 || members.Any(m => !Regex.IsMatch(m.Key, "^[a-zA-Z0-9_-]+$") || string.IsNullOrWhiteSpace(m.Name) || m.TelegramId is <= 0)
            || members.Select(m => m.Key).Distinct().Count() != members.Count
            || members.Where(m => m.TelegramId != null).Select(m => m.TelegramId).Distinct().Count() != members.Count(m => m.TelegramId != null))
            throw new ArgumentException("組員設定不可空白，key / Telegram ID 不可重複，ID 必須是正整數。");
        return members;
    }
    public static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, true);
    }
}
