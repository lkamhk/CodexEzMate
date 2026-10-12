# Reset 動態：EzMate 接收端

接收端已隨 1.21.9 發佈；1.22.2 開發版改為 Codex Resets 公開 API，並新增通知小視窗，詳見 [API 及彈窗說明](codex-resets-api.md)。

收集器／Feed 服務開發者請參閱 [Reset 接收端接口文檔](reset-feed-api.md)，包含完整欄位限制、HTTP 契約、通知規則及聯調範例。

## 使用方式

在通知區圖示按右鍵，選擇 **Reset 動態**；用量詳細視窗亦有同名入口。

動態頁只保留 **重新整理**；「查看範例」「返回實際資料」「檢查帳戶用量」按鈕已移除。
內建 `reset-feed.sample.json` 仍保留作開發測試資料，不再提供範例操作入口。

主畫面顯示簡短來源狀態、公告標題、一次時間、來源連結及來源分類。不再列出下載／產生時間、
空白的適用範圍、重複免責文字或重複網域。長摘要及修訂內容可展開 **詳情**；已知的適用限制、
更正／撤回及來源失效警告仍保留。簡化只作用於本機顯示，不會改寫 Feed、revision 或通知規則。
來源健康詳情可在狀態說明上停留查看，原始公告可由來源連結開啟。

在 **Feed 設定** 確認預填網址，或輸入其他可信 HTTPS JSON 網址，勾選啟用並儲存。
預設每 5 分鐘檢查，可設定 5–60 分鐘；背景輪詢另加 0–30 秒隨機延遲。
失敗後指數退避，間隔最多 60 分鐘再加隨機延遲；可手動「重新整理」。
設定和快取存於 `%APPDATA%\CodexUsageAssistant\ResetWatch\`。

首次成功讀取新網址或新 `feedId` 時只建立基準，不彈出歷史通知、不要求刷新帳戶。
之後只有新 ID 或增加修訂版本的事件才會處理。重啟會恢復已處理記錄。
預告、發放／更正／撤回、社群回報可分別設定通知；社群通知預設關閉。
帳戶 Reset 次數增加仍使用既有監測，通知開關獨立於公開 Feed。

## Feed v1 契約

請以 [reset-feed.sample.json](reset-feed.sample.json) 作為結構範例，勿當作正式公告來源。
收集端應提供單一、已完成跨來源去重的 JSON 文件，不要求任何 Codex 帳戶驗證。

| 層級 | 欄位 | 說明 |
| --- | --- | --- |
| Feed | `schemaVersion` | 必填整數，目前只接受 `1`。 |
| Feed | `feedId` | 必填穩定識別碼；更換會建立新的靜默基準。 |
| Feed | `generatedAt` | 必填 ISO 8601 時間，包含 UTC `Z` 或明確 offset；每次收集器檢查後更新。 |
| Feed | `sources` | 必填，1–32 個來源；即使沒有新消息也應更新來源健康狀態。 |
| Feed | `events` | 必填，0–200 個事件，可保留近期歷史。 |
| Source | `id`, `name`, `health` | 必填。`health` 為 `healthy`、`degraded` 或 `unavailable`。 |
| Source | `lastSuccessfulCheckAt`, `message` | 可選；標示 `healthy` 時必須有成功檢查時間。不可只因 Feed 發佈成功便標示來源健康。 |
| Event | `id`, `revision`, `title` | 必填。ID 不因轉載改變；修訂為大於 0 的整數，內容修改必須增加修訂。 |
| Event | `status` | 必填：`preview`、`announced`、`corrected`、`retracted`。 |
| Event | `evidence` | 必填：`official`、`corroborated`、`community`。這是收集端分類，不是 EzMate 獨立核實結果。 |
| Event | `impacts` | 必填、非空、不重複：`global`、`banked`、`policyChange`，可包含多個。 |
| Event | `publishedAt`, `updatedAt` | 必填。公開消息原始時間及修訂時間，後者不得早於前者。 |
| Event | `summary`, `scope`, `timeDescription` | 可選純文字。資格不明時保留未知；模糊時間保持原文，不虛構精確倒數。 |
| Event | `expectedStartAt`, `expectedEndAt` | 可選時間；都有提供時，開始不得晚於結束。 |
| Event | `relatedEventId` | 可選關聯 ID，用於收集端關聯預告／後續事件；接收端目前不另行顯示關聯樹。 |
| Event | `links` | 必填，1–10 個；每個包含 `sourceId`、`label`、`url`，指向已列入 `sources` 的來源。 |

ID 最長 128 字元，僅接受 ASCII 英數字及 `- _ . :`。
事件標題最多 240 字元，摘要 4,000 字元；時段說明及適用範圍各 1,000 字元。
所有來源連結及 Feed 網址必須是 HTTPS，不接受 URL 帳密、fragment 或控制字元。
文字只呈現為文字；不執行 HTML、Markdown 內嵌程式、腳本或 Feed 中的指令。

接收端拒絕重複 JSON 屬性、重複來源／事件 ID、缺少必要欄位、未知 enum／schema、
非法連結、無效時間及超過 512 KiB 的回應；解壓及串流後同樣受限。
同一 `feedId` 的 `generatedAt` 不得倒退，事件修訂不得降低；
同修訂的內容變更亦會被拒絕。拒絕後保留上次有效快取並顯示錯誤。
v1 允許未使用的額外欄位，方便加入不影響既有語意的擴充。

## 通知與帳戶行為

只推送最近 24 小時內新出現或已修訂的事件；已過期且有結束時間的預告不推送。
Feed 和事件至少一個關聯來源都必須於最近一小時更新／成功檢查。
過期或不健康的來源不會被解讀為「没有新公告」；仍然近期的未處理事件會等待恢復再判斷。

同批最多在一個通知列出三個事件，其他事件在動態視窗查看。
通知使用既有 Windows 通知區機制；系統通知設定及專注模式可能影響實際顯示。
本機記錄最多保留 2,048 個事件修訂，優先保留 Feed 仍有列出的事件。
先保存處理記錄再投遞通知：避免重啟洗版，但若恰好在保存後崩潰，該次彈出通知可能未送達。
這不是可靠訊息佇列或 exactly-once delivery；事件仍可在動態頁查看。

`announced`／`corrected` 且被 Feed 標記為官方或交叉核實的額度恢復／Banked Reset 事件，
只會向既有用量刷新循環提出合併請求。社群、預告、撤回及單純政策變更不會自動要求帳戶刷新。
收集器的分類是信任邊界；不要使用來歷不明的 Feed。

**接收端不自動兌換 Reset，不直接啟動或續跑 Goal，不把公開公告當成帳戶已到帳。**
既有 Goal 監控若已由使用者開啟，仍可在其自身確認實際額度恢復後，按原設定處理。
本階段沒有新增逐事件的帳戶到帳關聯判定；帳戶結果在既有用量詳情確認。

## 網絡、快取及生命週期

HTTP 使用 15 秒整體逾時、`ETag`／`If-None-Match` 及 `Last-Modified`／`If-Modified-Since`。
HTTP 304 必須有有效快取才能接受，且不會把來源原有時間更新成剛剛檢查過。
自動重新導向關閉，請填寫可直接取得 JSON 的網址；非成功狀態均顯示檢查失敗。
桌面端不發送 Codex token、cookie、帳戶 ID、對話或用量；Feed 主機仍看得到一般連線資料，例如 IP。
來源金鑰或秘密不應放入 Feed 或網址。未啟用時不輪詢、不連線。

手動與背景 Feed 檢查共用鎖，不會並行下載。關閉應用會取消進行中的請求。
設定／快取以同目錄暫存檔寫入後替換，寫入失敗時不覆蓋上次有效內容。

## 測試

```powershell
dotnet test tests/CodexUsageAssistant.Tests/CodexUsageAssistant.Tests.csproj --filter "FullyQualifiedName~ResetFeedTests|FullyQualifiedName~ResetWatchTests|FullyQualifiedName~ResetWatchUiTests|FullyQualifiedName~ResetActivityPresentationTests"
```

測試以模擬 HTTP、記憶體儲存及測試自有資料夾運行，不依賴即時公開來源或真實 Codex 帳戶。
WPF 測試檢查英文、繁體及簡體介面、最小寬度、單一工具列按鈕、長內容／異常狀態，以及單純呈現資料不觸發網絡或帳戶操作。
如設定 `CODEX_RESET_UI_PREVIEW_ROOT`，WPF 測試亦會輸出活動頁與設定頁的 PNG 預覽。

後續部署收集端只需遵守 v1 契約，提供 HTTPS Feed，再於 EzMate 設定網址即可。
