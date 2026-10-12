# Codex Resets API 與通知小視窗（1.22.2）

預設來源改為 `https://codex-resets.com/api/v1/status`。
[API 文件](https://codex-resets.com/api/docs)及 [OpenAPI 規格](https://codex-resets.com/api/openapi.json)由 Codex Resets 網站提供，毋須 API key。
網站註明不隸屬 OpenAI；此 API 並非 OpenAI 官方帳戶用量 API，不能確認個別帳戶的 reset 是否已到帳。
EzMate 會顯示「網站彙整 · 非官方 API」，並保留 **Data from Codex Resets** 連結及原文連結。

## 資料

- `latest_reset`：网站彙整的最近已執行 reset。
- `scheduled_reset`：已排程但仍等待執行證據；時間過咗亦唔會自動當成已發放。
- `active_watch`：網站 AI 分類預測，明確標示為預測，並非 OpenAI 承諾。EzMate 本身不呼叫 AI。

亦支援手動填寫 `https://codex-resets.com/api/v1/resets?limit=100` 讀取一頁歷史公告（最多 100 項）；預設 status 端點用於監察最新狀態。
其他 HTTPS 網址繼續沿用 EzMate 自訂 Feed 格式。
曾儲存舊預設 IP Feed 網址的設定會自動移轉；自訂網址、啟用狀態、通知偏好保留。

## 彈窗

「Reset 動態 → Feed 設定」新增「有新公告時顯示小視窗」，預設開啟。
Feed 本身仍須啟用；首次讀取或轉換來源只建立基準，不重播歷史公告。
之後符合通知偏好的新公告／修訂會在目前螢幕右下角顯示小視窗：

- 不搶鍵盤焦點，不新增工作列按鈕。
- 預設保持開啟直到按 × 關閉；如在 Feed 設定啟用「自動關閉」，可設定 1–3600 秒後關閉，滑鼠移入會暫停倒數。
- 「查看 Reset 動態」開啟完整資料及来源連結。
- 同時多項更新合併顯示，避免堆疊大量視窗。
- 關閉此選項則沿用 Windows 系統匣通知。

通知按事件 ID 及內容修訂去重，重開程式亦保留記錄。
HTTP 304 保留事件內容；HTTP 429 會遵守有效 Retry-After 的等待時間。
網站 API 資料只用作顯示及通知，不會直接兌換 reset、發送對話或啟動 Goal。

這是尚未打包的 1.22.2 開發版。先正常退出舊 EzMate，再執行 `啟動Feed通知預覽.bat` 可試用已準備的 Debug 預覽。

## 1.22.5 測試通知

`啟動Feed通知預覽.bat` 現在會傳入 `--preview-reset-notice`，直接顯示有「測試通知」標示的本機示範小視窗。
程式尚未啟動時會先啟動；已有 **1.22.5 或以上** 運行時會將測試要求交給現有程序，不再開啟會話管理器。
舊程序未支援此指令，升級時須先從通知區正常退出，再啟動新版。
亦可使用系統匣右鍵選單的「測試通知小視窗」。示範不需要 Feed 啟用或出現新公告，不修改真實通知去重記錄。
