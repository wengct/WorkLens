---
name: worklens-project-guidelines
description: 在 WorkLens repository 中實作功能、修正問題、審查程式碼或更新文件時，套用本專案的架構、程式風格、測試、資料安全與發行規範。
---

# WorkLens 專案基本規範

在 WorkLens repository 內工作時套用以下規範。依照使用者指定的範圍執行，並保留工作目錄中不相關的既有變更。

## 依職責放置程式碼

WorkLens 是 .NET 10 Blazor Server 應用程式。

- Razor 頁面與共用 UI 元件放在 `Components/`；版面配置與導覽放在 `Components/Layout/`。
- UI 事件處理函式應保持精簡。可重複使用的業務行為應透過聚焦的介面放在 `Services/`。
- 實體、領域概念與列舉放在 `Domain/`。
- SQLite 初始化與相容性升級放在 `Data/`，並透過 `DatabaseInitializer` 實作。
- 靜態 CSS、JavaScript 與隨附的瀏覽器資源放在 `wwwroot/`。
- 跨平台安裝、執行與煙霧測試腳本放在 `scripts/`。
- xUnit 測試放在 `tests/WorkLens.Tests/`，不要讓測試原始碼進入網站專案的編譯範圍。

新增抽象層之前，先檢查鄰近程式碼，沿用現有的模組邊界與命名方式。

## 保護相容性與本機資料

- 資料庫升級必須採新增式、可重複執行，並確保既有安裝可安全升級。除非使用者明確要求變更資料，否則保留既有工作項目、佐證資料、報告、排程及備份。
- 測試不得依賴開發者的 WorkLens 資料；使用暫存目錄或記憶體內 SQLite 資料庫。
- 不得提交資料庫、log、報告、備份、repository 路徑、作者電子郵件、AI 提示詞或回應、cookie、token、憑證、瀏覽器設定檔及本機 `appsettings` 覆寫設定。
- 執行期間的資料必須保存在平台專屬的 WorkLens 資料目錄，而非 repository 內。
- 除非需求明確改變此產品決策，否則維持僅繫結 loopback 位址及本機優先的隱私模式。

## 遵循程式與文件慣例

- C# 使用四個空白縮排、nullable reference types、implicit usings 及 file-scoped namespace。
- 公開型別與成員使用 PascalCase；區域變數與私有欄位使用 camelCase；非同步方法名稱以 `Async` 結尾。
- Razor 路由檔案使用 PascalCase；CSS class 使用小寫 kebab-case。
- 使用者介面與文件採用繁體中文（台灣）用語，並與既有內容保持一致。
- 使用者要求建立 commit 時，保持每個 commit 聚焦，並採 Conventional Commits 風格的祈使句主旨，例如 `feat: add ...` 或 `fix: correct ...`。
- 對外可見的行為變更應更新 `CHANGELOG.md`。

## 依變更風險進行測試

修正錯誤、資料庫升級、排程規則及呈現設定時應加入回歸測試。xUnit 測試類別以受測主體命名；測試方法以情境與結果命名，例如 `Next_run_moves_to_the_next_selected_day`。

先執行範圍最小且相關的驗證，再依風險擴大範圍：

```powershell
dotnet restore WorkLens.csproj
dotnet build WorkLens.csproj --no-restore
dotnet test tests/WorkLens.Tests/WorkLens.Tests.csproj --no-restore
git diff --check
```

需要對齊 CI 或準備發布時，使用 Release 設定執行測試：

```powershell
dotnet test tests/WorkLens.Tests/WorkLens.Tests.csproj --configuration Release
```

交付時說明已執行及無法執行的檢查。除非使用者明確要發布，否則不要推送語意化版本 tag；符合 `vX.Y.Z` 的 tag 會觸發發行工作流程並發布支援平台的安裝套件。
