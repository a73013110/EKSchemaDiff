# EKSchemaDiff

> SQL Server 結構比對與發版差異 CLI — 連兩個資料庫、互動勾選、產生官方部署 SQL 與暖色系差異報告。
> by **Yikai** · MIT License

`eksd` 是一支終端機工具，用微軟官方的 **DacFx**（Visual Studio「結構描述比較」的同一個引擎）比對兩個 SQL Server 資料庫，讓你像 VS 一樣勾選要更版的物件、預覽差異，最後一次輸出：

- **單一部署 SQL**（依相依順序，官方引擎產生，非手寫）
- **依序切分的個別 SQL 檔**（檔名標好順序，直覺知道怎麼一步步執行）
- **逐物件差異 HTML 報告 + 總覽**（暖色系，左更版／右原版）

相比舊的「VS 匯出 → 拆分 → 手動快照 → 差異」流程，`eksd` 把這些收斂成一支指令，而且**資料表（欄位、限制、描述、索引）也能比對**——這是舊流程做不到的。

## 為什麼安全

- **SQL 由 DacFx 產生**，與 VS Schema Compare 相同引擎，不是工具自己拼字串。
- **預設忽略權限**：`ignorePermissions = true`，比對與部署 SQL 都不會產生 `GRANT/DENY/REVOKE`。這直接杜絕「用沒開權限的開發庫比對正式庫，結果產出移除權限的 SQL，害其他廠商無法使用」的事故。雙保險：預設也把 `Permissions/Users/Logins/RoleMembership/Credentials` 整類排除。
- **預設不刪物件**：`dropObjectsNotInSource = false`、`blockOnPossibleDataLoss = true`。
- 切分後做**嚴格批次驗證**，確保切分前後操作內容與順序完全一致。

## 安裝

需求：Windows、[.NET 10 SDK/Runtime](https://dotnet.microsoft.com/)（自行建置）或直接用發佈的單檔 exe。

```powershell
# 自行建置單檔 exe（self-contained，免裝 runtime）
dotnet publish src/EKSchemaDiff.Cli/EKSchemaDiff.Cli.csproj -c Release -r win-x64 `
  --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

把 `publish\eksd.exe` 放到任一資料夾，將該資料夾加入 `PATH` 環境變數，之後在任何終端機輸入 `eksd` 即可啟動。

> **關於檔案大小**：發佈後約 170MB，其中絕大部分是 **DacFx 本身**（內含 T-SQL 解析器與結構模型，約 150MB+），不是 .NET runtime。因此改用 framework-dependent（`--self-contained false`）幾乎無法縮小，卻會多一個「目標機器須先裝 .NET 10」的前提——所以這裡**建議用 self-contained**，同樣大小但免裝任何東西。
>
> 若你的機器都已裝 .NET 10、想要 framework-dependent 版：
> ```powershell
> dotnet publish src/EKSchemaDiff.Cli/EKSchemaDiff.Cli.csproj -c Release --self-contained false -p:PublishSingleFile=true -o publish
> ```

## 快速開始

直接執行 `eksd` 進入**主選單**，全部作業都能從選單完成，不必先記指令：

```powershell
eksd
```

主選單提供：

- **▶ 開始比對** — 連線並比對，進入「勾選 + 即時預覽」畫面後匯出
- **⚙ 設定選項** — 調整比對與輸出選項（每項有中文說明）
- **✎ 新增/編輯連線設定** — 引導式填入伺服器、資料庫、帳號、密碼，自動寫入 `.eksd.json`（不需手動編輯檔案）
- **≡ 列出 profile**

> 第一次使用：選「新增/編輯連線設定」填好來源與目標即可（任一欄位輸入 `:q` 可隨時取消返回）。也可用 `eksd init` 產生範本後手動編輯。
> 快速啟動／CI：`eksd compare --profile <名稱>`（指定 profile 時跳過主選單）。

### 「勾選 + 預覽」畫面操作

| 按鍵 | 動作 |
|---|---|
| ↑ / ↓ | 移動游標（下方即時顯示該物件差異預覽） |
| 空白 | 勾選 / 取消該物件 |
| A / N | 全選 / 全部取消 |
| Enter | 確認，前往匯出 |
| Esc | 取消 |

> 下方預覽為 unified 風格：每行最左為**行號**（`+`/context 顯示更版行號、`-` 顯示原版行號），`-` 為原版、`+` 為更版；完整差異請見匯出的 HTML 報告。

## 設定檔（`.eksd.json`）

`eksd` 會從目前目錄往上層尋找 `.eksd.json`（像 `.git`/`.editorconfig`），找不到時退回全域 `%USERPROFILE%\.eksd\config.json`。同名 profile 由專案層覆寫全域。

一個設定檔可放多組具名 profile，對應不同專案或情境：

```jsonc
{
  "version": 1,
  "defaultProfile": "uat2prod",
  "profiles": [
    {
      "name": "uat2prod",
      "description": "UAT → PROD",
      "source": { "server": "XXX.XXX.XXX.XXX", "database": "Sample_DB_UAT",     "auth": "integrated" },
      "target": { "server": "XXX.XXX.XXX.XXX", "database": "Sample_DB_PROD", "auth": "integrated" },
      "outputDir": "EKSchemaDiff輸出",
      "compareOptions": {
        "ignorePermissions": true,
        "dropObjectsNotInSource": false,
        "blockOnPossibleDataLoss": true,
        "ignoreExtendedProperties": false,
        "excludedObjectTypes": ["Permissions", "Users", "Logins", "RoleMembership", "Credentials"]
      },
      "exportOptions": {
        "deployScript": "Both",
        "exportHtml": true,
        "deployDatabaseName": null,
        "groupSplitByObject": true
      }
    }
  ]
}
```

- **來源（source）= 更版內容的依據**（差異報告左側）；**目標（target）= 被更新對象**（右側）。
- 切換情境：`eksd --profile uat2prod`，或啟動後用方向鍵挑。

### 部署資料庫名稱不同（USE 覆寫）

部署 SQL 與每個切分檔頂端都會帶 `USE [資料庫];`。預設用**目標資料庫名稱**，但有時你內部的目標庫名與**客戶端實際庫名不同**（例如你這邊叫 `App_PROD`，客戶那邊叫 `App`）。這時設定 `exportOptions.deployDatabaseName`，所有輸出的 `USE` 就會改用這個名字：

```jsonc
"exportOptions": { "deployDatabaseName": "App" }   // 交付給客戶執行時 USE [App];
```

留空（`null`）＝沿用目標庫名。也可在**設定頁**直接編輯這一項；比對情境面板會顯示實際會用的「部署 USE 資料庫」。

### 同物件批次併入同一檔

`groupSplitByObject`（預設 `true`）：同一個物件的多個批次會合併到**同一個切分檔**。例如改了一張資料表的欄位、又改了它某欄位的描述，DacFx 會產生分散的兩個批次；開啟此選項後它們會併到該資料表的同一個檔（如 `01_ALTER_TABLE_dbo_DemoTable.sql` 內同時含 `ALTER TABLE` 與 `sp_addextendedproperty`），不會散成多檔。

> 分組後切分檔的順序會與 `FullScript.sql` 不同（依物件歸檔），但切分嚴格驗證會確認**每個批次內容不增、不減、不被竄改**。`FullScript.sql` 仍維持 DacFx 的相依順序，是權威的單一部署腳本。

### 連線與密碼

- 支援 SQL 帳密（`auth: "sql"`）或 Windows 整合驗證（`auth: "integrated"`）。
- 設定檔放在**本機**，可直接填明碼密碼：

```jsonc
"source": { "server": "...", "database": "...", "auth": "sql", "user": "app", "password": "你的密碼" }
```

- 若想避免明碼（例如要把設定檔分享給他人），`password` 可改用環境變數插值 `${env:EKSD_PWD}`；缺密碼時 `eksd` 會在執行當下互動詢問（不回顯）。

## 指令

| 指令 | 說明 |
|---|---|
| `eksd` | 主選單（開始比對／設定／新增連線／列出 profile） |
| `eksd compare` | 直接比對並匯出（不經主選單；可加 `--profile`、`--yes`） |
| `eksd config` | 設定頁，調整比對與輸出選項並存回 |
| `eksd init` | 在目前目錄建立 `.eksd.json` 範本 |
| `eksd profiles` | 列出已發現的 profile |

`compare` 常用選項：`--profile <名稱>`、`--out <目錄>`、`--export single|split|both`、`--yes`（非互動，CI 用）。

## 輸出結構

```
<outputDir>/
  FullScript.sql              # 單一完整部署 SQL（依相依順序）
  切分SQL/
    00_切分摘要.csv
    01_ALTER_TABLE_dbo_DemoTable.sql
    02_ADD_DESCRIPTION_DemoTable_DemoFlag.sql
    03_ALTER_PROCEDURE_dbo_uspGetDemoTable.sql
    ...
  差異報告/
    00_差異比對總覽.html
    01_dbo_DemoTable.html
    ...
```

## 比對選項一覽（`compareOptions`）

| 選項 | 預設 | 說明 |
|---|---|---|
| `ignorePermissions` | `true` | 不比對/不產生 GRANT/DENY/REVOKE |
| `dropPermissionsNotInSource` | `false` | 即使比權限也不刪目標多出的權限 |
| `ignoreRoleMembership` / `ignoreLoginSids` | `true` | 不碰角色成員、登入 SID |
| `ignoreExtendedProperties` | `false` | 比對 MS_Description（資料表/欄位描述） |
| `blockOnPossibleDataLoss` | `true` | 可能掉資料時阻擋 |
| `dropObjectsNotInSource` | `false` | 不刪目標多出的物件 |
| `ignoreWhitespace` | `true` | 忽略空白差異 |
| `excludedObjectTypes` | 權限/帳號類 | 整類排除（雙保險） |

## 專案結構

```
src/
  EKSchemaDiff.Core/     # DacFx 比對包裝、設定、比對選項、SQL 切分器
  EKSchemaDiff.Report/   # LCS 差異引擎 + 暖色 HTML 樣板
  EKSchemaDiff.Cli/      # Spectre.Console 互動 TUI 與命令
tests/
  PipelineCheck/         # 離線管線測試（切分器 + 報告，免連線）
```

## 授權

MIT — 詳見 [LICENSE](LICENSE)。
