using EKSchemaDiff.Core.Splitting;
using EKSchemaDiff.Report;

// 離線驗證切分器與 HTML 報告（不需連線資料庫）。
// 模擬 DacFx GenerateScript() 風格的部署腳本：含 SET 標頭、PRINT 進度、資料表 ALTER、
// 描述 sp_addextendedproperty、Procedure ALTER；刻意不含 GRANT/DENY 以驗證權限已被忽略。

const string synthetic = """
/*
Deployment script for Sample_DB_PROD
*/
GO
SET ANSI_NULLS, ANSI_PADDING, ANSI_WARNINGS, ARITHABORT, CONCAT_NULL_YIELDS_NULL, QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
GO
USE [Sample_DB_PROD];
GO
PRINT N'正在變更 [dbo].[DemoTable]...';
GO
ALTER TABLE [dbo].[DemoTable] ADD [DemoFlag] INT NULL;
GO
PRINT N'正在建立 [dbo].[DemoTable].[DemoFlag] 的描述...';
GO
EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'示範說明', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'DemoTable', @level2type = N'COLUMN', @level2name = N'DemoFlag';
GO
PRINT N'正在變更 [dbo].[uspGetDemoTable]...';
GO
ALTER PROCEDURE [dbo].[uspGetDemoTable]
AS
    SELECT DemoId, Status, DemoFlag FROM dbo.DemoTable;
GO
PRINT N'正在刷新相依模組...';
GO
EXEC sp_refreshsqlmodule N'[dbo].[vwDemoTableSummary]';
GO
EXEC sp_refreshsqlmodule N'[dbo].[vwDemoTableDetail]';
GO
""";

Console.OutputEncoding = System.Text.Encoding.UTF8;
var outDir = Path.Combine(Path.GetTempPath(), "eksd-pipelinecheck");
if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
Directory.CreateDirectory(outDir);

// 1) 切分
var split = ScriptSplitter.Split(synthetic, "Sample_DB_PROD");
Console.WriteLine("=== 切分結果 ===");
foreach (var f in split.Files)
    Console.WriteLine($"  {f.FileName}  [{f.Action} {f.ObjectType} {f.ObjectName}]");
Console.WriteLine($"嚴格驗證：{(split.VerificationPassed ? "通過" : "未過：" + split.VerificationMessage)}");

bool hasPerm = System.Text.RegularExpressions.Regex.IsMatch(
    synthetic, @"(?im)^\s*(GRANT|DENY|REVOKE)\b");
Console.WriteLine($"權限語句：{(hasPerm ? "有（不預期）" : "無（正確）")}");

foreach (var f in split.Files)
{
    var p = Path.Combine(outDir, f.FileName);
    File.WriteAllText(p, f.Content, new System.Text.UTF8Encoding(true));
}

// 2) 資料表差異 HTML（這是現行流程做不到的核心）
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
var htmlPath = Path.Combine(outDir, "table_diff.html");
File.WriteAllText(htmlPath, html, new System.Text.UTF8Encoding(true));
Console.WriteLine("\n=== 資料表差異 HTML ===");
Console.WriteLine($"  差異列數：{diffCount}（含逗號變更 + 新增 DemoFlag 欄位）");
Console.WriteLine($"  輸出：{htmlPath}");

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

// 依物件分組後：DemoTable 的 ALTER TABLE 與其 DemoFlag 描述合併成同一檔 →
// 預期 3 檔：ALTER TABLE DemoTable（含描述）/ ALTER PROCEDURE uspGetDemoTable / 合併的 REFRESH。
int expectedFiles = 3;
var customerFile = split.Files.FirstOrDefault(f => f.ObjectType == "TABLE" && f.ObjectName.Contains("DemoTable"));
bool tableMergedDesc = customerFile is not null
    && customerFile.Content.Contains("sp_addextendedproperty")
    && customerFile.Content.Contains("DemoFlag");
var refreshFiles = split.Files.Where(f => f.ObjectType == "SQLMODULE").ToList();
bool refreshGrouped = refreshFiles.Count == 1 && refreshFiles[0].FileName.Contains("2_OBJECTS");
bool ok = split.VerificationPassed && split.Files.Count == expectedFiles && diffCount >= 1
          && !hasPerm && tableMergedDesc && refreshGrouped;
Console.WriteLine($"\n整體：{(ok ? "PASS" : "FAIL")}（切分檔數={split.Files.Count}，預期={expectedFiles}；描述併入資料表檔={tableMergedDesc}；refresh 合併={refreshGrouped}）");
return ok ? 0 : 1;
