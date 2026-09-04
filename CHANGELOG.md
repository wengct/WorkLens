# 變更紀錄

WorkLens 的所有重要變更皆記錄於此文件。

## [尚未發布]

## [1.3.1] - 2026-09-05

### 修正

- 修正 Windows 使用者或安裝路徑包含非 ASCII 字元時，`worklens` 指令因批次檔路徑損壞而無法啟動。
- Windows 背景程序不再共用啟動 PowerShell 的 console，避免關閉終端機時連帶停止網站；安裝煙霧測試會確認程序隔離與安裝器結束後的健康狀態。

## [1.3.0] - 2026-09-05

### 修正

- Codex 來源現在能以唯讀方式掃描仍由 Codex 寫入的索引與會話檔案，不再因 Windows 檔案共享模式而略過進行中的會話。
- Azure DevOps PR 身分解析改用 `az ad signed-in-user show` 取得目前登入者的 Entra 身分與 UPN，不再要求 `az devops user show`；Entra ID 不會誤拿來和 ADO `createdBy.id` 比對，PR 本人篩選改用 UPN／帳號欄位核對，Graph 身分資料不可用時會顯示警告。
- Azure DevOps PR 與關聯 work item 的連結會優先使用 Web UI URL；若 CLI 只回傳 `/_apis/` API URL，會轉換為可直接開啟的 PR／work item 頁面，並依資源類型重新建立舊資料連結，避免 work item 誤連到 PR。

### 新增

- 新增 Azure DevOps PR 資料來源，可設定多組 Project／Repo／target branch，僅收集 `az login` 目前使用者建立的 PR，並取得 PR 直接關聯的 work item 欄位與 relations。
- Azure DevOps PR 設定頁新增 Azure CLI、Azure DevOps CLI extension 與登入身分檢測，並以輸入的 Organization URL 直接載入 Projects；同時提供各平台安裝說明（包含 `az extension add --name azure-devops` 與 `az login`）。

## [1.2.2] - 2026-09-04

### 修正

- Windows 更新安裝現在會使用新套件內的管理腳本停止舊版，讓 PID 檔已遺失或舊停止邏輯失效的安裝也能自動復原。

## [1.2.1] - 2026-09-04

### 修正

- 修正 Windows 更新安裝停止背景排程時可能遺留舊版行程、刪除 PID 檔並持續占用連接埠，導致新版啟動及 rollback 皆失敗。

## [1.2.0] - 2026-09-04

### 變更

- AI 工作回報上下文現在會帶入允許提供給 AI 的專案名稱，讓產生內容能正確標示各筆人工紀錄與來源活動所屬專案。
- 系統預設工作回報 Prompt 新增每日回報的標題、日期、專案及工作項目 Markdown 格式；仍使用舊系統預設的設定與範本會自動升級，自訂內容維持不變。
- 將每日與每週備份整合為單一「自動備份」排程；可複選星期，選滿七天即為每天備份，既有每週備份設定會合併後停用並保留歷史紀錄；所有排程改為緊湊的單欄設定列，由上而下排列。
- 每日與每週報告頁面及排程卡片現在會顯示資料涵蓋範圍與重複執行規則。
- AI Provider 設定現在可儲存多組具名設定、指定唯一的全域預設組，並將 AI 報告整理啟用狀態改為不受預設組切換影響的全域開關；既有單筆設定會自動保留並移轉為預設組。
- AI 設定頁將報告整理改為緊湊的狀態控制列，並修正設定清單、編輯區塊與環境診斷面板的間距，以及窄螢幕排列。
- AI 設定新增 OpenAI、Azure OpenAI、Anthropic、Google Gemini 與 OpenAI Compatible API Provider，可設定模型、推理能力、Endpoint 及本機加密保存的 API Key；ask-bridge 的檔案附件流程維持不變。
- AI 整理現在透過 Provider Adapter registry 與統一執行入口選擇執行環境，並將 ask-bridge 與其 ChatGPT、Gemini、Claude 目標服務分開建模，為後續加入其他 Provider 保留擴充點。
- 原「AI 整理」設定頁更名為「AI 設定」，並重新區分報告整理、Provider、AI 服務與 ask-bridge 執行選項。
- AI 設定頁新增依作業系統顯示的 ask-bridge 安裝步驟、驗證方式與官方 GitHub 套件來源。
- 將來源設定由六種平台專屬選項簡化為 Git 或 Codex，並可選填 WSL 位置；系統會自動對應原生平台，並以漸進方式顯示進階設定。
- 來源名稱現在為選填；省略時，系統會自動提供具描述性的預設名稱。
- Windows 與 macOS 的一鍵安裝程式現在會將 `worklens` 命令目錄加入使用者的 `PATH`，重新安裝時不會產生重複項目。

### 修正

- 修正視窗高度不足時，固定側邊欄無法捲動，導致部分選單與底部版本資訊無法查看。
- 修正舊版資料庫升級後，新增或重新偵測 AI Provider 設定時因遺留的 `Enabled` 欄位而無法儲存。

## [1.1.0] - 2026-09-04

### 新增

- 提示詞範本庫，提供建立、編輯、複製、測試、設為預設及封存等操作。
- 可設定每日報告、每週報告、每日備份及每週備份的排程。
- 排程執行紀錄，包含階段狀態、錯誤詳細資訊、下次執行時間及手動執行功能。
- 每個排程與手動執行皆可選擇 AI 提示詞，並在 AI 工作紀錄中保留提示詞快照。
- 可在執行期間設定備份資料夾，並提供寫入驗證及原生資料夾選擇器。
- 在側邊欄顯示發布版本，版本資訊取自 GitHub Release 的建置版本。
- 提示詞範本、排程定義及排程執行的領域詞彙表。

### 變更

- 排程報告現在會先建立制式報告，再由 AI 整理內容並自動儲存結果。
- AI 執行失敗時會保留制式報告並記錄失敗資訊，不再捨棄仍可使用的輸出。
- 現有的一般、每日及每週提示詞設定會自動移轉為提示詞範本。
- 報告操作現在採用精簡且對齊的工具列、語意更清楚的「重產制式摘要」文字，以及明確的儲存成功訊息。
- GitHub 與版本資訊現在共用側邊欄底部的同一個精簡列。
- 破壞性及覆寫操作改用可重複使用的互動視窗進行確認。
- 重設檢查點、刪除工作項目、刪除手動來源及變更報告期間時，會防止重複送出。

### 資料庫

- 新增 `PromptTemplates`、`ScheduleDefinitions` 及 `ScheduleExecutions` 資料表。
- 在 `AiJobs` 中新增提示詞識別資訊與內容快照欄位。
- 資料庫變更皆採新增方式，並會在首次啟動時自動套用；現有的工作項目、佐證資料、報告及備份均會保留。

### 修正

- Windows 背景排程不再因切換至電池供電而停止，且在使用電池時仍可啟動。
- 還原中斷的排程執行時，現在會清楚標示其狀態，不再持續顯示為執行中。
- Windows 備份資料夾選擇視窗會透過置頂的擁有者視窗顯示於最前方。
- SQLite 排程紀錄排序不再嘗試於伺服器端執行不支援的 `DateTimeOffset` 排序。
- 側邊欄的發布版本現在會正確呈現數值，而非 Razor 運算式文字。

[尚未發布]: https://github.com/wengct/WorkLens/compare/v1.2.2...HEAD
[1.2.2]: https://github.com/wengct/WorkLens/compare/v1.2.1...v1.2.2
[1.2.1]: https://github.com/wengct/WorkLens/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/wengct/WorkLens/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/wengct/WorkLens/compare/v1.0.3...v1.1.0
