using ConsoleKit.Text;
using EKSchemaDiff.Core.Scripting;
using EKSchemaDiff.Report;

// 離線驗證「逐物件腳本整理器」與 HTML 報告（不需連線資料庫）。
// 模擬 DacFx 對「單一物件」GenerateScript() 的輸出：含 SQLCMD 部署樣板（:setvar / :on error）、
// SQLCMD 模式偵測、PRINT 進度、USE [$(DatabaseName)]、資料表 ALTER、描述 sp_addextendedproperty。
// BuildObjectFile 應：去掉樣板與 PRINT、改套乾淨 USE+SET 標頭、保留 ALTER 與描述、命名為 ALTER_TABLE、驗證通過。

const string singleObject = """
/*
Deployment script for App
*/
GO
SET ANSI_NULLS, ANSI_PADDING, ANSI_WARNINGS, ARITHABORT, CONCAT_NULL_YIELDS_NULL, QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO
:setvar DatabaseName "App"
:on error exit
GO
IF N'$(__IsSqlCmdEnabled)' NOT LIKE N'True'
    BEGIN
        PRINT N'必須啟用 SQLCMD 模式才能成功執行此指令碼。';
        SET NOEXEC ON;
    END
GO
USE [$(DatabaseName)];
GO
PRINT N'正在改變 資料表 [dbo].[DemoTable]...';
GO
ALTER TABLE [dbo].[DemoTable] ADD [DemoFlag] INT NULL;
GO
EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'示範說明', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'DemoTable', @level2type = N'COLUMN', @level2name = N'DemoFlag';
GO
""";

Console.OutputEncoding = System.Text.Encoding.UTF8;
var outDir = Path.Combine(Path.GetTempPath(), "eksd-pipelinecheck");
if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
Directory.CreateDirectory(outDir);

// 1) 逐物件整理（USE 覆寫成 App_PROD，模擬 deployDatabaseName）
var f = DeployScriptBuilder.BuildObjectFile(singleObject, "App_PROD", "[dbo].[DemoTable]");
Console.WriteLine("=== 逐物件整理結果 ===");
Console.WriteLine($"  檔名：{f.FileName}");
Console.WriteLine($"  動作/類型/名稱：{f.Action} / {f.ObjectType} / {f.ObjectName}");
Console.WriteLine($"  操作批次數：{f.OperationBatchCount}");
Console.WriteLine($"  驗證：{(f.VerificationPassed ? "通過" : "未過：" + f.VerificationMessage)}");
File.WriteAllText(Path.Combine(outDir, f.FileName), f.Content, new System.Text.UTF8Encoding(true));

bool usesOverrideDb = f.Content.Contains("USE [App_PROD]") && !f.Content.Contains("$(DatabaseName)");
bool keptAlter = f.Content.Contains("ALTER TABLE [dbo].[DemoTable]");
bool keptDesc = f.Content.Contains("sp_addextendedproperty") && f.Content.Contains("DemoFlag");
bool strippedSqlCmd = !f.Content.Contains(":setvar") && !f.Content.Contains(":on error") && !f.Content.Contains("NOEXEC");
bool strippedPrint = !f.Content.Contains("PRINT N'正在改變");
bool namedAlterTable = f.FileName.Contains("ALTER_TABLE") && f.FileName.Contains("DemoTable");
bool hasPerm = System.Text.RegularExpressions.Regex.IsMatch(f.Content, @"(?im)^\s*(GRANT|DENY|REVOKE)\b");

Console.WriteLine($"  USE 覆寫={usesOverrideDb}；保留ALTER={keptAlter}；保留描述={keptDesc}；去SQLCMD={strippedSqlCmd}；去PRINT={strippedPrint}；命名={namedAlterTable}；含權限={hasPerm}");

// 2) 資料表差異 HTML（現行 PS1 流程做不到的核心）
const string oldTable = """
CREATE TABLE [dbo].[DemoTable] (
    [DemoId] INT NOT NULL,
    [Status] NVARCHAR(50) NULL
);
""";
const string newTable = """
CREATE TABLE [dbo].[DemoTable] (
    [DemoId] INT NOT NULL,
    [Status] NVARCHAR(50) NULL,
    [DemoFlag] INT NULL
);
""";
var (html, diffCount) = HtmlReportBuilder.BuildObjectReport(
    "[dbo].[DemoTable]", "變更", newTable, oldTable, false, new DateTime(2026, 6, 13, 10, 0, 0));
File.WriteAllText(Path.Combine(outDir, "table_diff.html"), html, new System.Text.UTF8Encoding(true));
Console.WriteLine($"\n=== 資料表差異 HTML ===  差異列數：{diffCount}");

// 2.6) 差異折疊規劃（DiffView.Flatten）：大段未變更 + 中間一處變更。
//      折疊模式應壓出折疊摘要列且總列數遠少於完整模式；完整模式應保留所有行、無折疊列。
var baseLines = Enumerable.Range(1, 40).Select(i => $"line {i}").ToList();
var changed = baseLines.ToList();
changed[19] = "line 20 CHANGED";   // 第 20 行內容變更
string baseText = string.Join("\n", baseLines);
string changedText = string.Join("\n", changed);

var foldRows = DiffEngine.Compare(changedText, baseText, false);
var folded = DiffView.Flatten(foldRows, full: false, contextLines: 3);
var entire = DiffView.Flatten(foldRows, full: true, contextLines: 3);

bool foldHasSummary = folded.Any(d => d.IsFold && d.HiddenCount > 0);
bool foldIsCompact = folded.Count < entire.Count;
bool fullKeepsAll = entire.Count == foldRows.Count && entire.All(d => !d.IsFold);
bool foldKeepsChange = folded.Any(d => d.Row?.Kind == DiffKind.Modified);
Console.WriteLine($"\n=== 差異折疊 DiffView.Flatten ===");
Console.WriteLine($"  折疊列數={folded.Count}（含摘要={foldHasSummary}）；完整列數={entire.Count}；" +
                  $"折疊更精簡={foldIsCompact}；完整保留全部={fullKeepsAll}；折疊保留變更={foldKeepsChange}");

// 2.5) 完整部署腳本清理（CleanFullScript）：應去 SQLCMD 樣板、註解標頭、PRINT，
//      改套單一 USE [部署庫] 開頭，保留所有 ALTER / sp_refreshsqlmodule / 描述批次與其順序。
const string fullScript = """
/*
Sample_DB_PROD 的部署指令碼
此程式碼是由工具所產生。
*/
GO
SET ANSI_NULLS, ANSI_PADDING, ANSI_WARNINGS, ARITHABORT, CONCAT_NULL_YIELDS_NULL, QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO
:setvar DatabaseName "Sample_DB_PROD"
:on error exit
GO
IF N'$(__IsSqlCmdEnabled)' NOT LIKE N'True'
    BEGIN
        PRINT N'必須啟用 SQLCMD 模式才能成功執行此指令碼。';
        SET NOEXEC ON;
    END
GO
USE [$(DatabaseName)];
GO
PRINT N'正在改變 資料表 [dbo].[DemoForm]...';
GO
ALTER TABLE [dbo].[DemoForm] ADD [TEST] VARCHAR (50) CONSTRAINT [DF_DemoForm_TEST] DEFAULT ('') NOT NULL;
GO
PRINT N'正在重新整理 檢視表 [dbo].[vwDemoQuery01]...';
GO
EXECUTE sp_refreshsqlmodule N'[dbo].[vwDemoQuery01]';
GO
""";

var cleanedFull = DeployScriptBuilder.CleanFullScript(fullScript, "Sample_DB_PROD");
File.WriteAllText(Path.Combine(outDir, "完整部署腳本.sql"), cleanedFull, new System.Text.UTF8Encoding(true));
bool fullStartsWithUse = cleanedFull.TrimStart().StartsWith("USE [Sample_DB_PROD];", StringComparison.Ordinal);
bool fullNoSqlCmd = !cleanedFull.Contains(":setvar") && !cleanedFull.Contains(":on error")
                    && !cleanedFull.Contains("$(DatabaseName)") && !cleanedFull.Contains("NOEXEC");
bool fullNoPrint = !cleanedFull.Contains("PRINT N'正在");
bool fullKeptOps = cleanedFull.Contains("ALTER TABLE [dbo].[DemoForm]")
                   && cleanedFull.Contains("sp_refreshsqlmodule N'[dbo].[vwDemoQuery01]'");
bool fullNoComment = !cleanedFull.Contains("此程式碼是由工具所產生");
Console.WriteLine($"\n=== 完整腳本清理 CleanFullScript ===");
Console.WriteLine($"  USE開頭={fullStartsWithUse}；去SQLCMD={fullNoSqlCmd}；去PRINT={fullNoPrint}；保留操作={fullKeptOps}；去註解標頭={fullNoComment}");

// 2.7) LayeredConfigStore 離線測試（探索往上層、全域+專案合併、存檔 round-trip；不碰真實使用者目錄）
var cfgRoot = Path.Combine(outDir, "config-test");
var cfgGlobal = Path.Combine(cfgRoot, "global.json");
var cfgSub = Path.Combine(cfgRoot, "proj", "sub");
Directory.CreateDirectory(cfgSub);
Directory.CreateDirectory(Path.GetDirectoryName(cfgGlobal)!);
File.WriteAllText(cfgGlobal, """{"items":["g1"]}""");
File.WriteAllText(Path.Combine(cfgRoot, "proj", ".test.json"), """{"items":["p1"]}""");

var cfgOptions = new ConsoleKit.Configuration.LayeredConfigOptions
{
    ProjectFileName = ".test.json",
    GlobalConfigPath = cfgGlobal,
    JsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true },
};
var cfgStore = new ConsoleKit.Configuration.LayeredConfigStore<TestConfig>(
    cfgOptions,
    () => new TestConfig(),
    (g, p) => new TestConfig { Items = (g?.Items ?? new()).Concat(p?.Items ?? new()).ToList() });

var snap = cfgStore.Discover(cfgSub);   // 從子目錄起，應往上層找到 proj/.test.json
bool cfgFoundProject = snap.ProjectConfigPath is not null && snap.ProjectConfigPath.EndsWith(".test.json", StringComparison.Ordinal);
bool cfgMerged = snap.Effective.Items.Contains("g1") && snap.Effective.Items.Contains("p1");
var savedPath = snap.SaveProject(new TestConfig { Items = { "p2" } });
var reread = cfgStore.Discover(cfgSub);
bool cfgRoundTrip = File.Exists(savedPath) && reread.ProjectConfig is not null && reread.ProjectConfig.Items.Contains("p2");

Console.WriteLine($"\n=== LayeredConfigStore 離線測試 ===");
Console.WriteLine($"  探索往上層={cfgFoundProject}；全域+專案合併={cfgMerged}；存檔 round-trip={cfgRoundTrip}");

// 3) 總覽
var overview = HtmlReportBuilder.BuildOverview(
    new[]
    {
        new ReportIndexRow { Sequence = "01", Action = "變更", ObjectType = "Tables",
            ObjectName = "[dbo].[DemoTable]", Status = "有差異", DifferenceRows = diffCount, ReportFile = "table_diff.html" },
    },
    "uat2prod", new DateTime(2026, 6, 13, 10, 0, 0));
File.WriteAllText(Path.Combine(outDir, "00_總覽.html"), overview, new System.Text.UTF8Encoding(true));

Console.WriteLine($"\n所有輸出：{outDir}");

bool ok = f.VerificationPassed && usesOverrideDb && keptAlter && keptDesc
          && strippedSqlCmd && strippedPrint && namedAlterTable && !hasPerm && diffCount >= 1
          && fullStartsWithUse && fullNoSqlCmd && fullNoPrint && fullKeptOps && fullNoComment
          && foldHasSummary && foldIsCompact && fullKeepsAll && foldKeepsChange
          && cfgFoundProject && cfgMerged && cfgRoundTrip;
Console.WriteLine($"\n整體：{(ok ? "PASS" : "FAIL")}");
return ok ? 0 : 1;

sealed class TestConfig
{
    [System.Text.Json.Serialization.JsonPropertyName("items")]
    public List<string> Items { get; set; } = new();
}
