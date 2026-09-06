# GitHub Copilot 本機格式驗證

這份紀錄只描述檔案結構，不包含使用者提示詞、助理回覆、資料庫或本機完整路徑。

## 已驗證的 VS Code Insiders

VS Code Insiders 的 `workspaceStorage/<workspace-id>/chatSessions/<session-id>.jsonl` 是可重播的 JSONL 操作紀錄。實際樣本包含：

- `kind: 0` 初始化 Session 根物件；根物件的 `version` 為 3，並包含 `sessionId`、`creationDate`、`responderUsername`、`requests`。
- `kind: 1` 設定巢狀欄位；`kind: 2` 對陣列追加或插入；`kind: 3` 刪除欄位。
- Copilot 身分可由 `responderUsername`，或 request 的 agent／extension 識別欄位明確確認；不以 `modelId` 單獨判定。
- request 的 `message.text` 是使用者提示詞；response 的一般文字區塊是可見助理回覆。`thinking`、工具 invocation、inline reference、progress、附件區塊會被排除。
- 時間欄位可用 ISO 文字或 Unix 毫秒。日期歸屬優先採最早可見訊息時間，沒有訊息時間時採 Session `creationDate`。

因此 WorkLens 的 VS Code adapter 會先重播操作紀錄，再依 Copilot 身分擷取可見對話。JSON 快照也使用相同的訊息模型。

## Visual Studio 2026 關卡結果

提供的 Visual Studio 2026 非空 Session 檔案沒有副檔名，也不是 UTF-8／JSONL；結構分析確認它是由多個值串接而成的 MessagePack 二進位串流，並使用 .NET MessagePack timestamp extension。可辨識的結構包含：

- Session 層級的 `TimeCreated`、`TimeUpdated`、`ConversationMode`、`Responders` 及 Copilot service name。
- 訊息記錄的 `CorrelationId`、`MessageId`、`Content`、`Author`、`Model`、`SessionId` 等欄位。
- `Content` 內同時存在可見使用者／助理文字與工具／服務相關記錄，必須依結構化欄位及角色過濾，不能擷取零散字串當作完整對話。

這個結果足以確認讀到亂碼是因為直接以文字編碼開啟二進位檔。日期應以 `TimeCreated` 作為 Session 建立時間、`TimeUpdated` 作為更新時間；若格式沒有逐則訊息時間，不自行推算每一則的時間。

提供的 Visual Studio 2026 Chat before／after 檔案使用同一個 Session GUID；after 檔案保留相同的 TimeCreated，更新 TimeUpdated，並在串流尾端增加新的 user／assistant event。這確認同一個檔案會以完整 MessagePack 快照持續更新。

提供的 Agent（Preview）樣本使用相同的 MessagePack 外框與可見 Content 結構；其 SelectedAgent.Service.Name 為 Microsoft.VisualStudio.Copilot.CopilotCliResponder，一般 Chat 則為 Microsoft.VisualStudio.Copilot.CopilotChatAgentProvider。因此可由 responder 辨識 Agent（Preview），但仍使用相同的結構化訊息解析器。

Visual Studio 2026 Chat 與 Agent（Preview）格式關卡已通過，WorkLens 現在可以解析並匯入這些 session。讀取期間若檔案變動或尾端截斷，會保留可讀快照、標記不完整並安排重試。

## 支援狀態

| 用戶端 | 本機格式 | WorkLens 狀態 |
| --- | --- | --- |
| Copilot CLI／App | `session-state/**/events.jsonl` | 已支援可見對話匯入 |
| VS Code Stable／Insiders | `workspaceStorage/*/chatSessions` JSON／JSONL | 已支援快照與操作重播 |
| Visual Studio 2026 | MessagePack 二進位串流 | 已支援；已驗證同 GUID 持續更新 |
| Visual Studio 2026 Agent（Preview） | MessagePack 二進位串流；Copilot CLI responder | 已支援；已驗證與 Chat 共用結構 |
