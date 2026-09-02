# WorkLens

WorkLens 是一個以本機優先為設計的個人工作歷程與工時回報網站。

## 隱私與版控邊界

此 repository 僅包含程式碼與安全的預設設定。執行期間的資料會儲存在 repository 外部，預設位置為：

`%LOCALAPPDATA%\WorkLens`

此目錄包含 SQLite 資料庫、報告、備份、每日實體 log 及本機整合狀態，並已刻意排除在版控之外。請勿將以下內容提交至 Git：

- 工作紀錄、報告、匯出檔案或資料庫檔案；
- Git 作者 email、repository 路徑或私人專案識別資訊；
- AI prompt、回應內容、瀏覽器設定檔、cookie、token 或憑證；
- 本機 `appsettings` 覆寫設定或憑證檔案。

`ask-bridge` 負責管理自己的瀏覽器設定檔與登入狀態。WorkLens 只會呼叫 CLI，不會將 cookie 或憑證複製到 repository。

若需要查看錯誤與啟動紀錄，請檢查：

`%LOCALAPPDATA%\WorkLens\logs\worklens-YYYY-MM-DD.log`

## 執行

```powershell
dotnet run
```

開啟 `http://127.0.0.1:5077`。應用程式只會繫結至 loopback 位址。請從網頁介面設定資料來源與 AI；第一次啟動時不會自動收集任何來源。

## 日常操作

開啟首頁就是「今日工作台」：可選擇日期、選填專案、輸入時數與 Markdown 工作內容，適合連續補登。Git 來源活動會依日期與專案自動顯示並納入報告，不需要人工關聯。

「工作摘要」將工作歷程與摘要放在同一頁：可用月曆選擇每日或每週期間、依專案與關鍵字篩選左側歷程，並在右側產生、編輯、AI 整理與匯出完整期間摘要。篩選不會改變摘要涵蓋的資料範圍；回補來源可針對單日或整週執行，且不會改變正常收集的 checkpoint。

資料來源也支援 Windows Codex 與 WSL Codex。系統會從 `sessions` 與 `archived_sessions` 讀取本機會話、以 session id 去重，並保存 User／Codex 的可見文字對話；每日、每週及 AI 報告上下文只使用 User 訊息。Codex 資料目錄可自動偵測 `CODEX_HOME`／`~/.codex`，也可在來源設定中覆寫。

報告在人工紀錄或來源資料有實質異動時會標示為「需要重產」；重產會覆蓋同一期間的內容，並在覆蓋前要求確認。AI 整理可在設定中調整通用 Prompt，或分別覆寫每日與每週 Prompt；系統仍固定驗證 JSON、工時與工作紀錄 ID。
