# WorkLens

WorkLens 是一個以本機優先為設計的個人工作歷程與工時回報網站。

## 一鍵安裝

GitHub Release 提供不需預先安裝 .NET 的 self-contained 版本。安裝完成後 WorkLens 會立即在本機啟動、開啟一次瀏覽器，並設定成目前使用者登入後自動在背景執行。

Windows PowerShell：

```powershell
irm https://raw.githubusercontent.com/wengct/WorkLens/main/scripts/get.ps1 | iex
```

macOS（自動選擇 Apple Silicon 或 Intel 版本）：

```bash
curl -fsSL https://raw.githubusercontent.com/wengct/WorkLens/main/scripts/get.sh | bash
```

安裝器會從公開 GitHub Release 下載套件並核對 `SHA256SUMS`。Windows 程式預設安裝在 `%LOCALAPPDATA%\Programs\WorkLens`，macOS 安裝在 `~/.local/share/worklens`；程式版本與執行資料分開保存。

目前的 Release 未經 Windows code signing 或 Apple notarization。作業系統可能在第一次執行時顯示 SmartScreen 或 Gatekeeper 警告；安裝腳本不會關閉或繞過任何系統安全功能。

### 管理、更新與移除

Windows：

```powershell
& "$HOME\bin\worklens.cmd" status
& "$HOME\bin\worklens.cmd" open
& "$HOME\bin\worklens.cmd" restart
& "$HOME\bin\worklens.cmd" stop
& "$HOME\bin\worklens.cmd" uninstall
```

macOS：

```bash
~/.local/bin/worklens status
~/.local/bin/worklens open
~/.local/bin/worklens restart
~/.local/bin/worklens stop
~/.local/bin/worklens uninstall
```

重新執行一鍵安裝命令即可升級。安裝器會保留上一版；新版無法通過健康檢查時會自動回復。預設解除安裝只移除程式與登入排程，不刪除工作資料；若確定要永久刪除資料，使用 `uninstall --purge-data` 並依提示輸入 `DELETE`。

如需停用登入自動啟動或安裝後不要開啟瀏覽器，可先下載腳本再帶參數：

```powershell
$installer = irm https://raw.githubusercontent.com/wengct/WorkLens/main/scripts/get.ps1
& ([scriptblock]::Create($installer)) -NoAutostart -NoOpenBrowser
```

```bash
curl -fsSL https://raw.githubusercontent.com/wengct/WorkLens/main/scripts/get.sh | bash -s -- --no-autostart --no-open-browser
```

`Git`、Node.js、Chrome 與 `ask-bridge` 不會由安裝器自動安裝。它們是資料來源或 AI 功能的選用相依項，WorkLens 會在設定頁個別偵測。

## 隱私與版控邊界

此 repository 僅包含程式碼與安全的預設設定。執行期間的資料會儲存在 repository 外部，預設位置為：

- Windows：`%LOCALAPPDATA%\WorkLens`
- macOS：`~/Library/Application Support/WorkLens`

此目錄包含 SQLite 資料庫、報告、備份、每日實體 log 及本機整合狀態，並已刻意排除在版控之外。請勿將以下內容提交至 Git：

- 工作紀錄、報告、匯出檔案或資料庫檔案；
- Git 作者 email、repository 路徑或私人專案識別資訊；
- AI prompt、回應內容、瀏覽器設定檔、cookie、token 或憑證；
- 本機 `appsettings` 覆寫設定或憑證檔案。

`ask-bridge` 負責管理自己的瀏覽器設定檔與登入狀態。WorkLens 只會呼叫 CLI，不會將 cookie 或憑證複製到 repository。

若需要查看錯誤與啟動紀錄，請檢查：

- Windows：`%LOCALAPPDATA%\WorkLens\logs\worklens-YYYY-MM-DD.log`
- macOS：`~/Library/Application Support/WorkLens/logs/worklens-YYYY-MM-DD.log`

## 執行

```shell
dotnet run
```

開啟 `http://127.0.0.1:5077`。應用程式只會繫結至 loopback 位址。請從網頁介面設定資料來源與 AI；第一次啟動時不會自動收集任何來源。

## 維護者發行

推送符合 `vX.Y.Z` 的 tag 後，GitHub Actions 會執行測試、安裝煙霧測試，建立 `win-x64`、`osx-x64`、`osx-arm64` 三個 self-contained 套件及 SHA-256 checksum，最後建立公開 Release：

```shell
git tag v1.0.0
git push origin v1.0.0
```

## 日常操作

開啟首頁就是「今日工作台」：可選擇日期、選填專案、輸入時數與 Markdown 工作內容，適合連續補登。Git 來源活動會依日期與專案自動顯示並納入報告，不需要人工關聯。

今日工作台也可直接貼上會議紀錄、討論內容或其他人工來源。人工來源會保存為來源活動並納入日／週摘要，但不會自行增加確認工時。

「工作摘要」將工作歷程與摘要放在同一頁：可用月曆選擇每日或每週期間、依專案與關鍵字篩選左側歷程，並在右側產生、編輯、AI 整理與匯出完整期間摘要。篩選不會改變摘要涵蓋的資料範圍；回補來源可針對單日或整週執行，且不會改變正常收集的 checkpoint。

資料來源支援 Windows、macOS 與 WSL 的 Git 和 Codex。系統會從 `sessions` 與 `archived_sessions` 讀取本機會話、以 session id 去重，並保存 User／Codex 的可見文字對話；每日、每週及 AI 報告上下文只使用 User 訊息。Codex 資料目錄可自動偵測 `CODEX_HOME`／`~/.codex`，也可在來源設定中覆寫。

報告在人工紀錄或來源資料有實質異動時會標示為「需要重產」；重產會覆蓋同一期間的內容，並在覆蓋前要求確認。AI 整理可在設定中調整通用 Prompt，或分別覆寫每日與每週 Prompt；系統仍固定驗證 JSON、工時與工作紀錄 ID。
