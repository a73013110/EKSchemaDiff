# 命名與用語規範

本文件定義 EKSchemaDiff / ConsoleKit 的命名與用語規律，目的有二：① 讓接手者能快速掌握專案規則；
② 讓中性骨架 `ConsoleKit` 能乾淨拆版到其他 CLI 專案。機械可強制的規則放在 [.editorconfig](.editorconfig)；
其餘（縮寫大小寫、繁中用語）靠 code review 把關。

## A. 命名空間
- 一專案一根命名空間 = 專案名；子資料夾與命名空間 1:1 對應。
- 中性骨架根命名空間為 `ConsoleKit`；領域程式為 `EKSchemaDiff.*`。
- `ConsoleKit` **不得**出現任何領域品牌字串（`eksd`/`EKSchemaDiff`/`.eksd`/`EKSD_` 等）。

## B. 縮寫大小寫（.NET 官方規則）
- 二字母縮寫**全大寫**：`UI`、`ID`、`IO`（如 `ConsoleUI`）。
- 三字以上縮寫**僅首字大寫**：`Html`、`Sql`、`Csv`、`Json`（如 `HtmlReportBuilder`）。

## C. 型別角色後綴
- 命令：`<動詞>Command` + 對應 `<動詞>Settings`（如 `CompareCommand`/`CompareSettings`）。
- 全螢幕互動流程：`*Screen`（如 `ReviewScreen`）；就地編輯器：`*Editor`（如 `SettingsEditor`）。
- 可重用 UI 元件無後綴：`Menu`、`Banner`、`ConsoleUI`、`TextInput`。
- 產生器/引擎/比較器：`*Builder`、`*Engine`、`*Comparer`。
- 資料載體：`*Summary`、`*Progress`、`*Session`、`*Difference`、`*Snapshot`。
- 列舉：`*Kind`、`*Mode`。
- 設定模型用語義最簡名（**不**加 `Config` 贅字）：`Profile`、`CompareOptions`、`ExportOptions`、`ConnectionConfig`；根設定 `EksdConfig`。

## D. 成員命名
- public 成員 PascalCase；方法以動詞開頭。
- 私有欄位 `_camelCase`（含鎖物件 `_gate`）；區域變數/參數 camelCase。
- 常數 PascalCase；**例外**：P/Invoke 原生常數沿用 Win32 的 `SCREAMING_CASE`（如 `STD_OUTPUT_HANDLE`），於原檔註解標明。
- 布林：`Is/Has/Can/Should*`；嘗試型：`Try*` + `out`；工廠：`Create/Build/From*`；入口：`Run/Show`。

## E. 檔案組織
- 一檔一個「主要」公開型別，檔名 = 型別名。
- 緊耦合的小列舉或 `*Settings` 可與其主型別同檔（如 `ExportItemState` 與 `Exporter`、`CompareSettings` 與 `CompareCommand`）。

## F. 相依注入（DI）原則
- 判準是**「是否需要替換、隔離測試或生命週期管理」**，而非單純「有無副作用」。
- 注入：`AppInfo`、`IAppLog`、`Banner`、`ConfigStoreFactory`、`CompareWorkflow`、命令。
- 保持 `static`：純工具（`ConsoleUI`/`Menu`/`TextInput`/`EnvInterpolation`），以及本階段無替換需求的無狀態服務（`SchemaComparer`/`Exporter`/`DeployScriptBuilder`/`DiffEngine`/`HtmlReportBuilder`——即使有外部副作用）。
- **勿為無替換需求的型別建介面**；不用 Generic Host、不用 assembly scanning。所有 DI 註冊集中於組合根單一處（`Program.cs`）。

## G. 結束碼
- 通用：`ConsoleKit.ExitCode`（`Ok=0`、`UsageError=1`、`SoftwareError=70`）。
- 領域：`EKSchemaDiff.Cli.EksdExitCode`（`CompareFailed=2`、`ExportFailed=3`、`VerificationFailed=4`）。
- 不在程式碼裡散落 magic number。

## H. 繁體中文用語表（glossary）
| 用語 | 意義 |
|---|---|
| 來源 / 更版 | source；更版內容的依據，差異報告左側 |
| 目標 / 原版 | target；被更新對象，差異報告右側 |
| 比對情境 | 一組具名 profile（來源→目標 + 選項） |
| 完整部署腳本 | DacFx 依相依順序產生的單一權威部署 SQL |
| 逐物件部署檔 | 以單一物件為主、可獨立執行的個別 SQL 檔 |
| 差異報告 | 暖色系逐物件 HTML + 總覽 |
| 設定頁 | 編輯比對/輸出選項的就地編輯畫面 |
| profile | 具名比對情境（程式內保留英文） |

## I. 註解
- 公開型別與非顯而易見的成員加繁中 `<summary>`，重點說明「**為什麼**」而非覆述程式碼。

## J. Spectre markup 安全（避免雙重跳脫）
- `MarkupLineInterpolated` / `MarkupInterpolated`：插值洞**會自動跳脫**，**不可**再手動 `Markup.Escape`（否則雙重跳脫）。
- `MarkupLine` / `Markup` / `new Rule(...)` / `.Title(...)` 等以字串組裝動態值時，動態值**必須** `Markup.Escape` 或 `ConsoleUI.Esc`。
- 只有開發者手寫的靜態樣式標籤（`[red]...[/]`）才視為可信 markup。

## K. 拆版到新 CLI（ConsoleKit 邊界）

`ConsoleKit` 是「中性骨架」：它對領域**零依賴**，可整包複製成任何新 CLI 的起點。維護時請守住下列邊界，避免領域回滲。

### K1. 依賴方向（單向，不可逆）
- `ConsoleKit` 為基礎，**無任何專案相依**、**不得**參考 `EKSchemaDiff.*`。
- `Report → ConsoleKit`、`Core → ConsoleKit + Report`、`Cli → ConsoleKit + Core + Report`。
- 新增 `ConsoleKit` 對任一領域專案的參考＝架構破壞，CI/review 必擋。

### K2. 什麼放骨架、什麼放領域
- **放 `ConsoleKit`**：與領域無關、可被任何 CLI 複用的能力——Host/DI、TUI 元件、佈景、記錄器、分層設定機制、**文字差異引擎（`ConsoleKit.Text`）**、**`${env:VAR}` 插值（`ConsoleKit.Configuration.EnvInterpolation`）**。判準：拿掉 EKSchemaDiff 仍成立。
- **放領域**：DacFx 比對、設定模型（`Profile`/`EksdConfig`…）、HTML 報告樣板、品牌字串與品牌色票。
- 灰色地帶以 §A.10「骨架不得出現品牌字串」為終審：只要需要寫出 `eksd`/`EKSchemaDiff`/領域語彙，就**不**屬於骨架。

### K3. 領域接骨架的三個介接點（照抄即可拆版）
1. **品牌與路徑** → 寫一份 `AppInfo`（record），交給 `ConsoleHost.Run`。骨架所有品牌/路徑/版號皆取自它。
2. **佈景色票** → 骨架 `Theme` 只內建中性預設 `ThemePalette.Neutral`；品牌外觀由領域寫一份 `ThemePalette`（如 `EksdTheme.Champagne`），在組合根以 `ConsoleHost.Run(..., theme: ...)` 注入。**不得**把品牌色票寫回 `ConsoleKit`。
3. **領域設定** → 骨架提供泛型 `LayeredConfigStore<TConfig>(options, createEmpty, merge)`；領域注入自己的 `TConfig` 與合併語義，再包出領域 API（參考 `ConfigStoreFactory`/`ConfigStore`）。

### K4. 共用引擎的單一真相來源
- 差異「計算」只有一處：`ConsoleKit.Text.DiffEngine`。TUI 預覽（`ReviewScreen`/`DiffScreen`）與 HTML 報告（`HtmlReportBuilder`）**必須**呼叫同一個 `DiffEngine.Compare`，僅「上色／呈現」各自實作。**嚴禁**在任一端另起爐灶複製 diff 演算法——否則預覽與報告會悄悄分岔。
