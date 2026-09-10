# 組員週報收件服務

.NET 10 本地常駐 Console Worker，Telegram long polling 收件，SQLite 保存版本，JSON 原文依週五日期歸檔。Codex 每週五 16:50 整理並在對話提供 Telegram 文字，由管理者 17:00 人工發送。

## 首次設定

1. 將 Telegram Bot Token 貼至 `config/bot-token.txt`，只放 Token 單行、不加引號。此檔案已排除 Git，亦可使用 `TELEGRAM_BOT_TOKEN` 環境變數（優先）。不要在 Codex 對話貼 Token。
2. 本機 `config/members.local.json` 已依提供名單建立；新環境從 `config/members.example.json` 複製，再填姓名。`key` 是穩定識別碼，開始收件後請勿任意更改。
3. 建置、啟動：

```sh
dotnet restore src/WeeklyReports
dotnet build src/WeeklyReports -c Release --no-restore
./scripts/run.sh
```

4. 請每位組員私訊 Bot `/start` 或 `/id`，取得自己的數字 ID。管理者核對本人後執行（將範例 ID 換成實際 ID）：

```sh
dotnet src/WeeklyReports/bin/Release/net10.0/WeeklyReports.dll bind member-1 123456789
```

三位組員對應 key 請見本機 members.local.json。綁定立即生效；未綁定者只能取得指引，不能提交。組員名稱不作為身份驗證依據。

## 組員操作

- `/template`：取得四區塊格式。標題獨立一行、每區必填；無事項填「無」。
- 直接貼完整週報：儲存並回覆收件確認。
- 重新提交完整週報或編輯原訊息：保留版本，取提交時間最新的版本。
- `/status`：查看當週最新收件與是否晚交。
- 第一版僅接收私訊文字，不解析圖片、附件或群組訊息。Telegram 訊息刪除不會刪除已存週報。

```text
1.進行中
- 項目、進度、預計完成日期

2.已完成
- 項目與完成結果

3.未完成
- 原訂本週完成事項、延期原因、需要協助

4.值班處理線上問題
- 日期、問題、處理結果與後續追蹤
```

## 週次與截止

Asia/Taipei 週一至週日歸屬該週週五，例：9/7～9/13 → `2026-09-11`。週末新提交仍為當週補交；跨週編輯依原訊息週次保存。跨週補前週請編輯前週原訊息。
提交時間及本地收件時間都必須 <= 週五 16:50:00，才能自動納入例行彙整。離線期間截止後才收到的資料也算晚交，以確保已產生的截止快照可重現。

```sh
./scripts/snapshot.sh --date 2026-09-11
# 管理者明確要求重新整理、納入晚交時使用：
./scripts/snapshot.sh --date 2026-09-11 --include-late
```

Snapshot 不使用 AI、不需要 Token、不發送訊息。AI 文字整理由 Codex 排程依 `docs/weekly-summary.md` 執行，無須另設 OpenAI API Key。

## 資料

```text
data/
  weekly-reports.db                       # SQLite 正式來源（WAL）
  reports/2026-09-11/
    members/member-1/123456.json           # 每個 Telegram update 一版，含原文和時間
    summary/
      report.md
      telegram.txt
      runs/20260911-165001/                # 每次整理及來源快照
```

SQLite 先成功寫入，再匯出原文，最後推進 Telegram offset。重送依 update ID 去重；若匯出中斷，下次重試／snapshot 可重建檔案。回覆確認可能因網路重試重複，但不會新增重複資料。`receiver.lock` 防止同專案重複收件。

## 常駐與通知

目前先提供前景啟動腳本，Token 填妥後即可啟動。關閉終端會停止服務；持續運作可用 macOS LaunchAgent，範本在 `docs/local.weeklyreports.plist`，安裝方式見下方。啟動 Bot 前請確保未被其他程式使用；已設定 webhook 時服務會停止，避免改動其他用途。

```sh
mkdir -p "$HOME/Library/LaunchAgents" data/logs
cp docs/local.weeklyreports.plist "$HOME/Library/LaunchAgents/local.weeklyreports.plist"
launchctl bootstrap "gui/$(id -u)" "$HOME/Library/LaunchAgents/local.weeklyreports.plist"
# 停止並移除載入：
launchctl bootout "gui/$(id -u)" "$HOME/Library/LaunchAgents/local.weeklyreports.plist"
```

LaunchAgent 登入後啟動，不會喚醒睡眠中的電腦。Mac 必須開機且連網；Codex 排程需要 App 運行，桌面推播亦取決於系統通知設定。Telegram 未收取更新最多保留 24 小時，長時間離線可能漏件：要求組員以 Bot 成功回覆為收件依據。

## 測試與備份

```sh
dotnet run --project tests/WeeklyReports.Tests
```

測試使用暫存目錄與合成資料，不傳 Telegram。真實收件需要 Token 和組員 ID 完成後再驗證。
Git 排除 Token、組員資料、data/ 與執行日誌。備份時先停止收件服務，再備份整個 data/ 與 members.local.json；不要只複製正在寫入的 .db 而漏掉 WAL。第一版不自動清除資料、不自動備份。

依據：[Telegram Bot API](https://core.telegram.org/bots/api#getupdates)、[Codex 排程文件](https://developers.openai.com/codex/app/automations)。
