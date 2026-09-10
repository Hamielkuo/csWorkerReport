# C# 組員週報

.NET 10 從 Trello TCT-CJ 唯讀取得卡片及移動歷史，固定人員與清單 ID 分類。Codex 每週五 16:50（Asia/Taipei）整理、存檔、在目前對話提供 Telegram 文字，由管理者 17:00 人工傳給主管。

Telegram Bot 收件已停用，原有 SQLite 與原文保留，不再納入新週報。`run` 指令已禁止啟動收件。舊操作記錄在 docs/telegram-legacy.md，僅作歷史參考。

## 設定與使用

- `config/trello.local.json`：API Key、唯讀 Token、固定看板 ID／名稱／shortLink。
- `config/trello-report.local.json`：三位人員 Member ID 與輸出姓名、五個清單 ID／精確名稱／分類，以及Worktrack 標籤。
- 設定範本在 config/*.example.json；本機秘密與人員資料均排除 Git。

```sh
dotnet restore src/WeeklyReports
dotnet build src/WeeklyReports -c Release --no-restore
# 正式報告：需已到當週週五 16:50
./scripts/snapshot.sh --date 2026-09-11
# 提前試跑，與正式檔案分開保存
./scripts/snapshot.sh --date 2026-09-11 --preview
# 原始唯讀連線驗證
dotnet src/WeeklyReports/bin/Release/net10.0/WeeklyReports.dll trello-check
```

## 固定規則

期間為上週五 16:50 之後～本週五 16:50，完成動作依左開右閉區間選取。

| 清單 | 分類／範圍 |
|---|---|
| 處理完畢 | 本期移入且截止時仍在此清單，才算已完成 |
| 發布 | 全列進行中，標示待發布／發布中 |
| 測試 | 全列進行中，標示 QA 測試中 |
| Work in process | 全列進行中，標示處理中 |
| To Do | 全列未完成，不推測延期 |
| 其他（含 Plan） | 排除 |

同一卡片多位指定人員並列一次，未指派指定三人者排除。以明確工單號去重，無工單號才以卡片 ID 去重。非「Worktrack」標籤均為系統名稱；「Worktrack」卡片只列第四節，保留狀態；無值班事項仍保留第四節並顯示「• 無符合條件的事項。」。已封存卡片（closed=true）一律排除，包含已完成與值班事項；來源快照仍可保留封存資料供截止檢查，但不得納入週報。

## 來源與輸出

卡片與動作採 ID cursor 分頁（每頁 1000），取得指定看板全量資料再套用人員、清單範圍。只呼叫該看板 GET API；憑證放 Authorization Header，禁止 redirect。

```text
data/reports/YYYY-MM-DD/summary/
  report.md / telegram.txt                  # 正式稿
  latest-run.txt                            # 正式來源目錄
  preview-report.md / preview-telegram.txt   # 提前試跑稿
  preview-latest-run.txt
  runs/<時間>-trello/
    source.json                             # 來源、動作與擷取時間
    classified.json                         # 分類與完成移入證據
    report.md / telegram.txt
    telegram-01.txt ...                      # 每則 <= 3500 UTF-16 code units
```

程式提供可直接使用的基礎稿；Codex 依 docs/weekly-summary.md 整理重點並保留基礎稿及全部卡片，無須 OpenAI API Key。正式與試跑各自保存，歷史版本不覆蓋。沒有符合卡片時仍產出零事項報告，不再列「未交名單」。

## 截止準確性與運作條件

Trello REST 不提供完整歷史快照（例如標籤變更不一定能從動作清單還原）。若全看板任何卡片最後活動／本期動作晚於截止時間，程式保守拒絕產生正式稿，包括範圍外卡片更新。試跑則以擷取開始為界。失敗不覆蓋既有成功結果，也不會改用目前資料宣稱為截止版本。

因此請讓 Mac 開機、連網、Codex App 運作並準時執行，且擷取時避免修改看板。任意日期的歷史補跑若缺少當時快照可能被拒絕；若已保存當期 source.json，可使用 `trello-report --date YYYY-MM-DD --source /absolute/path/source.json` 重現分類（來源需符合正式截止檢查）。REST 多次請求不是原子快照，無法保證任意時刻完整還原。

## 驗證與備份

```sh
dotnet run --project tests/WeeklyReports.Tests --no-restore
```

測試包括週界、完成退回、多人去重、範圍排除、值班、分頁、錯誤保護與輸出。備份 data/ 和本機設定需另外處理，Git 不包含它們。舊 Telegram SQLite 仍保留但不再寫入。

依據：[Trello API](https://developer.atlassian.com/cloud/trello/guides/rest-api/nested-resources/)、[Codex 排程](https://developers.openai.com/codex/app/automations)。

對外格式使用「C# 組員週報」，不顯示來源列或 Trello 連結；事項依「1. 姓名｜狀態｜系統｜事項」同一行呈現。來源與分類 JSON 仍保留卡片連結供追溯。

報告僅保留進行中、已完成、未完成、值班處理線上問題四節；不另列本週重點。「未完成」僅對應 To Do；空區塊均顯示「• 無符合條件的事項。」。

To Do 無任何指派人員（idMembers 為空）的未封存卡片，例外納入未完成，姓名欄寫「尚未安排」。已指派人員但不含指定三人者仍排除，其他清單的無人員卡片仍排除。「未完成」區塊事項採「1. 姓名｜系統｜事項」，省略狀態欄；進行中與值班區塊保留狀態。Worktrack 標籤仍優先獨立列於第四節避免重複。此例外優先於前述一般人員範圍及單行格式規則。

## 最新人員分組格式（優先於舊單行格式）
日期期間下方列「進行中 N｜已完成 N｜未完成 N｜值班處理線上問題 N」。依四區塊實際卡片 ID 計數；值班不計入其他分類，多人卡片只計一次，不以人次計數。
四節內以【姓名】分組；無人員 To Do 用【尚未安排】並排在該節最前。多人卡片使用【姓名甲、姓名乙】聯名群組，只列一次，不擅自移除負責人。
進行中及值班群組內依狀態再分組，依處理中、QA 測試中、待發布／發布中等階段排序；已完成與未完成不再加狀態子標題。每個子組編號從 1 開始，事項列不重複姓名或狀態。
系統名稱已出現在事項標題時不重複加前綴，未出現的系統標籤保留前綴。空區塊仍寫「• 無符合條件的事項。」。提前試跑標記前加 ⚠️。期間顯示起訖時間，不改變實際左開右閉的查詢邏輯。
不得依格式範例刪除實際卡片或改變指派；統計數字必須由資料產生，不寫死範例數量。

## 最新版型與同工單去重（取代前述衝突格式）
標題與期間後顯示「👨‍💻 N 人｜進行中 N｜已完成 N｜未完成 N｜線上問題 N」。人數是實際列出事項中的不同人員數，不含尚未安排；各類件數為去重後工單數，四類互斥。每個大分類前後用「━━━━━━━━━━」，無資料只寫「無」。
人員標題為「👤 姓名｜N 項」，聯名用「👤 姓名甲、姓名乙｜N 項」且只出現一次；N 是該群組實際明細筆數，聯名工單不再拆列或重複計算。尚未安排排未完成最前。
進行中及線上問題按狀態用【處理中】、【QA 測試中】、【待發布／發布中】等子標題；同人跨狀態連續編號，不重設。已完成及未完成按大分類連續編號。事項用「系統｜工單標題」，不重複人名、狀態。可精簡重複系統詞或潤飾，但不得改變事實、工單號、責任人或數量。
在已符合期間、人員與清單範圍的候選卡片內，辨識 #數字、需求／BUG 加數字、標題開頭系統英文加數字（至少四位）的工單號。無明確單號時保留卡片 ID，不用標題相似度猜測。相同工單號只保留一筆，優先採處理完畢 > 發布 > 測試 > Work in process > To Do；這是重複卡片的報表優先規則，不代表擅自更改 Trello。處理完畢候選仍必須符合本期完成證據，不能拿舊完成卡壓過本期工作。只要合併候選中有Worktrack 標籤，整筆僅列線上問題，狀態採最高階段。人員與系統合併保留，有指派人員時移除尚未安排；分類 JSON 的 SourceCardIds 保存合併來源。
數字只能從最終去重明細計算，整理後不得再刪除、增加或複製條目。正式與試跑都保持同規則；試跑仍顯示 ⚠️ 識別。
