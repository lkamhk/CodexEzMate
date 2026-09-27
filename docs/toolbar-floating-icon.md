# Codex 工具列浮動圖示

1.21.7 改用 EzMate 的獨立 WPF 浮動圖示，預設開啟；1.21.9 使用透明底、單色白色的 E 標誌及下方 mate 字樣。只要 EzMate 正在運行，前景的 Codex 桌面視窗右上方就會出現 logo，包括新對話頁。
此圖示是覆蓋在視窗上的獨立按鈕，不會修改 Codex 程式檔案，也不依賴項目 Action。
此桌面版本的 Codex 與 ChatGPT 共用程序，圖示跟隨整個桌面視窗，切換 Chat／Work 分頁時亦會顯示。

- 左鍵點擊：開啟或還原現有 Session Browser。
- 按住左鍵拖曳：調整位置，放開時儲存。滾輪不會調整位置。
- 右鍵：開啟、重設位置，或隱藏圖示。
- 通知區 EzMate 選單「Codex 工具列浮動圖示」：重新開啟或關閉。

預設圖示大小為 32 DIP，距離 Codex 用戶區右邊 200 DIP、頂部 62 DIP。它不會自動辨識工具列按鈕；如果與原有按鈕重疊，可拖到空白位置。
位置以右邊／頂部偏移儲存，隨視窗移動、縮放及 DPI 改變調整。Codex 失去前景、最小化或移至其他虛擬桌面時隱藏，回到前景時重新顯示。
設定保存在 EzMate 資料根目錄的 `settings/toolbar-icon.json`。Debug 版本會存於 Debug 輸出目錄的 `settings/`。

升級前請從通知區退出舊 EzMate，再執行本次 Release 的 installer，或解壓完整 portable ZIP 啟動外層 `CodexEzMate.exe`。
毋須設定 Codex Action。`--session-browser` 單一實例入口仍可供外部捷徑使用。

原始碼開發者可用 .NET 8 SDK 執行 `dotnet run --project src/CodexUsageAssistant.App`。功能使用既有 WPF／Windows API，沒有新增 runtime。
