using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace WeeklyReports;

public sealed class Store : IDisposable
{
    private readonly SqliteConnection db;
    private readonly string root;
    public Store(string root)
    {
        this.root = root;
        Directory.CreateDirectory(Path.Combine(root, "data"));
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "data/weekly-reports.db") }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS reports(update_id INTEGER PRIMARY KEY, member_key TEXT NOT NULL, friday TEXT NOT NULL, submitted_at TEXT NOT NULL, received_at TEXT NOT NULL, raw TEXT NOT NULL, sections TEXT NOT NULL, late INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS state(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }
    public long Offset
    {
        get { using var c = db.CreateCommand(); c.CommandText = "SELECT value FROM state WHERE key='offset'"; return long.TryParse(c.ExecuteScalar()?.ToString(), out var n) ? n : 0; }
        set { using var c = db.CreateCommand(); c.CommandText = "INSERT INTO state VALUES('offset',$v) ON CONFLICT(key) DO UPDATE SET value=$v"; c.Parameters.AddWithValue("$v", value.ToString()); c.ExecuteNonQuery(); }
    }
    public void Save(Report report)
    {
        using var c = db.CreateCommand();
        c.CommandText = "INSERT OR IGNORE INTO reports VALUES($id,$member,$friday,$submitted,$received,$raw,$sections,$late)";
        c.Parameters.AddWithValue("$id", report.UpdateId);
        c.Parameters.AddWithValue("$member", report.MemberKey);
        c.Parameters.AddWithValue("$friday", report.Friday);
        c.Parameters.AddWithValue("$submitted", report.SubmittedAt.ToUniversalTime().ToString("O"));
        c.Parameters.AddWithValue("$received", report.ReceivedAt.ToUniversalTime().ToString("O"));
        c.Parameters.AddWithValue("$raw", report.Raw);
        c.Parameters.AddWithValue("$sections", JsonSerializer.Serialize(report.Sections));
        c.Parameters.AddWithValue("$late", report.Late ? 1 : 0);
        c.ExecuteNonQuery();
        // Re-export from committed data: replay after a crash never changes an existing revision.
        Export(report.Friday);
    }
    public List<Report> Read(string friday)
    {
        using var c = db.CreateCommand();
        c.CommandText = "SELECT * FROM reports WHERE friday=$date ORDER BY submitted_at,update_id";
        c.Parameters.AddWithValue("$date", friday);
        using var r = c.ExecuteReader();
        var result = new List<Report>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3)), DateTimeOffset.Parse(r.GetString(4)), r.GetString(5), JsonSerializer.Deserialize<string[]>(r.GetString(6))!, r.GetInt64(7) != 0));
        return result;
    }
    public void Export(string friday)
    {
        foreach (var report in Read(friday))
            Rules.AtomicWrite(Path.Combine(root, "data/reports", friday, "members", report.MemberKey, $"{report.UpdateId}.json"), JsonSerializer.Serialize(report, Rules.Json));
    }
    public object Snapshot(DateOnly friday, bool includeLate, DateTimeOffset now)
    {
        var date = friday.ToString("yyyy-MM-dd");
        var all = Read(date);
        var cutoff = Rules.Cutoff(friday);
        var eligible = all.Where(r => r.ReceivedAt <= now && r.SubmittedAt <= now && (includeLate || (r.ReceivedAt <= cutoff && r.SubmittedAt <= cutoff))).ToList();
        var members = Rules.Members(root);
        return new {
            Friday = date, Cutoff = cutoff, GeneratedAt = now, IncludeLate = includeLate,
            Members = members.Select(m => new { m.Key, m.Name, Bound = m.TelegramId != null,
                Report = eligible.LastOrDefault(r => r.MemberKey == m.Key),
                LateUpdates = all.Count(r => r.MemberKey == m.Key && r.Late && r.ReceivedAt <= now) }).ToArray()
        };
    }
    public void Dispose() => db.Dispose();
}
