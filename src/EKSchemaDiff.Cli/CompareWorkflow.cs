using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EKSchemaDiff.Cli.Tui;
using EKSchemaDiff.Core.Compare;
using EKSchemaDiff.Core.Config;
using EKSchemaDiff.Core.Export;
using Spectre.Console;

namespace EKSchemaDiff.Cli;

/// <summary>比對 → 預覽勾選 → 匯出的共用流程，供主選單與 compare 命令使用（DI singleton）。</summary>
public sealed class CompareWorkflow
{
    private readonly IAppLog _log;
    private readonly Banner _banner;
    private readonly ConfigStoreFactory _configStores;

    public CompareWorkflow(IAppLog log, Banner banner, ConfigStoreFactory configStores)
    {
        _log = log;
        _banner = banner;
        _configStores = configStores;
    }

    /// <summary>
    /// 自設定探索並執行比對流程（供 compare 命令與主選單快速模式共用）：
    /// 探索 → 驗證有 profile → 解析（互動時可挑選）→ 解析輸出形式 → 執行。
    /// </summary>
    public int RunFromConfig(string? startDir, string? profileName, string? outOverride, bool interactive)
    {
        ConfigStore store;
        try { store = _configStores.Discover(startDir); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[{Theme.Danger}]設定載入失敗：{ex.Message}[/]");
            return ExitCode.UsageError;
        }

        if (store.Effective.Profiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{Theme.Warning}]尚未設定任何 profile。[/]");
            AnsiConsole.MarkupLine("請執行 [bold]eksd[/] 從主選單建立連線設定，或 [bold]eksd init[/] 產生範本。");
            return ExitCode.UsageError;
        }

        Profile? profile;
        try
        {
            profile = store.ResolveProfile(profileName);
            if (profile is null)
            {
                if (!interactive)
                    throw new InvalidOperationException("有多組 profile，請以 --profile 指定。");
                profile = Prompts.PickProfile(store.Effective.Profiles, _banner);
                if (profile is null) return ExitCode.Ok;   // 按 Esc 取消挑選
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[{Theme.Danger}]{ex.Message}[/]");
            return ExitCode.UsageError;
        }

        return Run(store, profile, outOverride, interactive);
    }

    public int Run(
        ConfigStore store, Profile profile,
        string? outOverride, bool interactive)
    {
        _log.Step($"開始比對流程 profile='{profile.Name}' interactive={interactive}");
        // 選完 profile 後清空畫面再顯示比對情境，避免主選單／profile 挑選殘留干擾。
        if (interactive && ConsoleUI.Interactive)
        {
            AnsiConsole.Clear();
            _banner.Compact();
        }
        ShowProfileSummary(profile);

        CompareSession? session = null;
        try
        {
            AnsiConsole.Status().Spinner(Spinner.Known.Dots)
                .Start("正在比對兩個資料庫結構…", _ =>
                {
                    session = SchemaComparer.Run(profile, Prompts.PromptPassword);
                });
        }
        catch (Exception ex)
        {
            _log.Error("比對階段失敗", ex);
            AnsiConsole.MarkupLineInterpolated($"[{Theme.Danger}]比對失敗：{ex.Message}[/]");
            return EksdExitCode.CompareFailed;
        }

        if (session is null || !session.IsValid)
        {
            AnsiConsole.MarkupLine($"[{Theme.Danger}]比對結果無效。[/]");
            foreach (var e in session?.GetErrors() ?? Enumerable.Empty<string>())
                AnsiConsole.MarkupLineInterpolated($"  [{Theme.Danger}]- {e}[/]");
            return EksdExitCode.CompareFailed;
        }

        foreach (var u in session.UnrecognizedExcludedTypes)
            AnsiConsole.MarkupLineInterpolated($"[{Theme.Warning}]警告：排除類型無法辨識，已忽略：{u}[/]");

        if (session.Differences.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{Theme.Success}]兩個資料庫結構一致，沒有差異。[/]");
            return ExitCode.Ok;
        }

        _log.Step($"比對完成，差異 {session.Differences.Count} 項");
        ShowDiffOverview(session);

        if (interactive)
        {
            // 勾選 → 解析相依 → 確認頁；確認頁可「返回重勾」（沿用上次勾選），故整段為迴圈。
            HashSet<ObjectDifference>? lastPicks = null;
            while (true)
            {
                _log.Step("進入勾選預覽畫面 ReviewScreen");
                var included = ReviewScreen.Run(
                    session.Differences, profile.ExportOptions.HtmlIgnoreWhitespace, _log, _banner, lastPicks);
                _log.Step($"離開勾選預覽畫面，結果={(included is null ? "取消" : included.Count + " 項")}");
                if (included is null)
                {
                    AnsiConsole.MarkupLine($"[{Theme.Warning}]已取消，未匯出。[/]");
                    return ExitCode.Ok;
                }
                if (included.Count == 0)
                {
                    AnsiConsole.MarkupLine($"[{Theme.Warning}]未勾選任何物件，已取消匯出。[/]");
                    return ExitCode.Ok;
                }
                lastPicks = included;

                // 解析勾選：算出實際要部署的完整物件集（系統會為部署安全自動補入相依），尚未提交。
                // 必須以 spinner 回饋，否則畫面會「一片黑」像當掉（ReviewScreen 離開時已 Clear()）。
                _log.Step($"解析勾選 {included.Count}/{session.Differences.Count} 項（自動補齊相依，差異多時較慢）");
                AnsiConsole.Clear();
                // 不墊尾端空白：下方的 AnsiConsole.Status() 互動式 spinner 會自行在上方墊一行，
                // 否則會出現兩列空白（見 Banner.Compact 的 trailingBlank 說明）。
                _banner.Compact(trailingBlank: false);
                InclusionResult? resolved = null;
                AnsiConsole.Status().Spinner(Spinner.Known.Dots)
                    .Start("正在套用勾選並整理相依物件…（為確保部署能順利執行，系統會自動補齊相依物件，請稍候）",
                        _ => resolved = session.ResolveInclusion(included));
                _log.Step($"解析完成：勾選 {resolved!.Picked.Count} 項、相依補入 {resolved.Dependencies.Count} 項、共 {resolved.All.Count} 項");

                // 相依確認頁：讓使用者在匯出前看清補了哪些。
                var decision = InclusionConfirmScreen.Run(resolved, _banner);
                if (decision == InclusionDecision.Cancel)
                {
                    _log.Step("使用者於確認頁取消匯出");
                    AnsiConsole.MarkupLine($"[{Theme.Warning}]已取消，未匯出。[/]");
                    return ExitCode.Ok;
                }
                if (decision == InclusionDecision.Back)
                {
                    _log.Step("使用者於確認頁返回重新勾選");
                    continue;   // session 未提交，仍是原始全量差異；帶 lastPicks 回勾選頁
                }

                session.CommitInclusion(resolved);   // 確認 → 提交，後續匯出以此為準
                _log.Step("套用勾選完成（已確認）");
                break;
            }
            // 輸出項目一律沿用設定頁（exportOptions）的設定（不再每次詢問，否則設定形同虛設）。
        }

        var outputDir = ResolveOutputDir(store, profile, outOverride);
        _log.Step($"開始匯出 {profile.ExportOptions.Describe()} outputDir='{outputDir}'");

        ExportSummary summary;
        try
        {
            if (interactive)
            {
                var (captured, cancelled) = RunExportWithProgress(session!, outputDir);
                if (cancelled || captured is null)
                {
                    AnsiConsole.MarkupLine($"[{Theme.Warning}]已中斷匯出（部分檔案可能已寫出）。[/]");
                    return ExitCode.Ok;
                }
                summary = captured;
            }
            else
            {
                summary = Exporter.Export(session!, outputDir, DateTime.Now);
            }
        }
        catch (Exception ex)
        {
            _log.Error("匯出階段失敗", ex);
            AnsiConsole.MarkupLineInterpolated($"[{Theme.Danger}]匯出失敗：{ex.Message}[/]");
            return EksdExitCode.ExportFailed;
        }

        _log.Step($"匯出完成，HTML {summary.HtmlReportCount} 份、逐物件部署檔 {summary.ObjectScriptCount} 個");
        ShowExportSummary(summary);

        if (interactive && summary.HtmlReportCount > 0)
        {
            var overview = Path.Combine(outputDir, "差異報告", "00_比對總覽.html");
            if (File.Exists(overview) && AnsiConsole.Confirm("要開啟比對總覽 HTML 嗎？", defaultValue: false))
                OpenFile(overview);
        }

        return summary.ObjectScriptVerificationPassed ? ExitCode.Ok : EksdExitCode.VerificationFailed;
    }

    /// <summary>UI 執行緒讀取用的不可變項目快照（避免與背景匯出執行緒共用可變物件）。</summary>
    private readonly record struct ItemSnap(
        string Group, string Label, ExportItemState State, bool Warned, bool IsDependency,
        DateTime? StartedAtUtc, TimeSpan? Elapsed);

    /// <summary>
    /// 在背景執行匯出，前景以自繪畫面顯示進度（含大 Logo），可按 Esc 中斷。
    /// 進度畫面預先列出所有待產生項目，完成者以刪除線槓掉，並用顏色區分未處理／處理中／已完成。
    /// 回傳 (摘要, 是否中斷)。中斷時摘要為 null。
    /// </summary>
    private (ExportSummary? Summary, bool Cancelled) RunExportWithProgress(
        CompareSession session, string outputDir)
    {
        using var cts = new CancellationTokenSource();
        var gate = new object();
        var snap = new List<ItemSnap>();
        void Report(IReadOnlyList<ExportItem> items)
        {
            var copy = items.Select(i => new ItemSnap(
                i.Group, i.Label, i.State, i.Warned, i.IsDependency, i.StartedAtUtc, i.Elapsed)).ToList();
            lock (gate) { snap = copy; }
        }

        ExportSummary? summary = null;
        Exception? error = null;
        var wall = System.Diagnostics.Stopwatch.StartNew();
        var task = Task.Run(() =>
        {
            try { summary = Exporter.Export(session, outputDir, DateTime.Now, Report, cts.Token); }
            catch (OperationCanceledException) { /* 使用者中斷 */ }
            catch (Exception ex) { error = ex; }
        });

        // 動畫在替代螢幕緩衝區重繪：即使內容超過視窗高度而捲動，也只影響該緩衝區，
        // 不會把每一幀的殘影留進正常捲動歷史。離開後再把最終清單乾淨地印到正常緩衝區。
        var last = new List<ItemSnap>();
        try
        {
            ConsoleUI.EnterAltScreen();
            // 隱藏游標 + 停用自動換行：物件名一長（差異多的庫常見），未停用換行時該列會折成兩實體列，
            // 撐破幀高、使逐格歸位漂移，整個進度畫面捲成空白。必須在 finally 還原。
            ConsoleUI.EnterRedrawMode();
            int tick = 0;
            while (!task.IsCompleted)
            {
                List<ItemSnap> cur;
                lock (gate) { cur = snap; }
                RenderExportFrame(cur, cts.IsCancellationRequested, tick, done: false);
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
                    cts.Cancel();
                Thread.Sleep(80);
                tick++;
            }
            lock (gate) { last = snap; }
            RenderExportFrame(last, cts.IsCancellationRequested, tick, done: !cts.IsCancellationRequested);
        }
        finally
        {
            ConsoleUI.ExitRedrawMode();   // 先恢復自動換行，再離開替代螢幕
            ConsoleUI.LeaveAltScreen();
        }

        task.Wait();
        wall.Stop();
        if (error is not null) throw error;

        if (last.Count > 0)
            LogExportTimings(last, wall.Elapsed, cts.IsCancellationRequested);

        if (!cts.IsCancellationRequested && last.Count > 0)
            PrintFinalChecklist(last);
        return (summary, cts.IsCancellationRequested);
    }

    /// <summary>
    /// 把每個匯出項目的耗時寫入記錄檔（依群組分段），最後附上總牆鐘時間。
    /// 供事後追查「哪一項拖慢、整輪花多久」——畫面上的即時計時跑完就消失，這裡才是永久紀錄。
    /// </summary>
    private void LogExportTimings(IReadOnlyList<ItemSnap> items, TimeSpan wall, bool cancelled)
    {
        _log.Info($"=== 匯出耗時明細{(cancelled ? "（已中斷）" : "")} ===");
        string? curGroup = null;
        foreach (var it in items)
        {
            if (it.Group != curGroup)
            {
                curGroup = it.Group;
                _log.Info($"[{it.Group}]");
            }
            var elapsed = it.Elapsed is { } e ? FormatElapsed(e) : "—";
            var state = it.State switch
            {
                ExportItemState.Done => it.Warned ? "完成(警告)" : "完成",
                ExportItemState.Skipped => "略過",
                ExportItemState.Running => "未完成",
                _ => "未執行",
            };
            _log.Info($"  {it.Label}{(it.IsDependency ? " (相依)" : "")}  {state}  {elapsed}");
        }
        _log.Info($"匯出總耗時 {FormatElapsed(wall)}");
    }

    /// <summary>
    /// 動畫結束後，把最終清單以靜態方式印到正常緩衝區（永久記錄）：完成項以綠勾＋刪除線呈現。
    /// 全部列出、不開窗——這是給使用者事後捲動檢視用，列數多時自然往下接續即可。
    /// </summary>
    private void PrintFinalChecklist(IReadOnlyList<ItemSnap> items)
    {
        AnsiConsole.Clear();
        _banner.Compact();
        AnsiConsole.MarkupLine($"[{Theme.Success}]✔ 產出完成[/]");
        AnsiConsole.WriteLine();

        string? curGroup = null;
        foreach (var it in items)
        {
            if (it.Group != curGroup)
            {
                curGroup = it.Group;
                AnsiConsole.MarkupLine($"[{Theme.Accent}]▌[/] [bold {Theme.Accent}]{Markup.Escape(it.Group)}[/]");
            }
            var label = ConsoleUI.Esc(ConsoleUI.Truncate(it.Label, Math.Max(10, ConsoleUI.Width - 18)));
            var line = it.State switch
            {
                ExportItemState.Skipped => $"  [{Theme.Warning}]⤼[/] [strikethrough {Theme.TextMuted}]{label}[/] [{Theme.Warning}](略過)[/]",
                _ when it.Warned => $"  [{Theme.Warning}]✓[/] [strikethrough {Theme.Warning}]{label}[/] [{Theme.Warning}](警告)[/]",
                _ => $"  [{Theme.Success}]✓[/] [strikethrough {Theme.TextMuted}]{label}[/]",
            };
            if (it.IsDependency) line += $" [{Theme.TextFaint}]·相依[/]";
            if (it.Elapsed is { } el) line += $" [{Theme.TextFaint}]({FormatElapsed(el)})[/]";
            AnsiConsole.MarkupLine(line);
        }
        AnsiConsole.WriteLine();
    }

    private static readonly string[] SpinnerFrames =
        { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    private void RenderExportFrame(
        IReadOnlyList<ItemSnap> items, bool cancelling, int tick, bool done)
    {
        ConsoleUI.BeginFrame();
        // 進度畫面資料密集：用精簡單行 banner，讓整幀高度可控、不超出視窗（超出會觸發捲動造成 logo 抖動）。
        _banner.Compact();

        string spin = done ? $"[{Theme.Success}]✔[/]" : $"[{Theme.Accent}]{SpinnerFrames[tick % SpinnerFrames.Length]}[/]";
        ConsoleUI.Line($"{spin} [{Theme.Accent}]產出進度[/]　[{Theme.TextFaint}]Esc 中斷[/]");
        ConsoleUI.Line();

        int total = Math.Max(1, items.Count);
        int finished = items.Count(i => i.State is ExportItemState.Done or ExportItemState.Skipped);
        int current = Math.Clamp(finished, 0, total);
        int barWidth = Math.Clamp(ConsoleUI.Width - 26, 10, 60);
        int filled = (int)Math.Round((double)current / total * barWidth);
        filled = Math.Clamp(filled, 0, barWidth);

        // 進度條：已完成段用實心；未完成段放一個來回跑動的指示點，即使卡在大物件也看得出在動。
        var empty = new System.Text.StringBuilder(new string('░', Math.Max(0, barWidth - filled)));
        if (!done && empty.Length > 0)
        {
            int span = empty.Length;
            int pos = tick % (span * 2);
            if (pos >= span) pos = span * 2 - 1 - pos;   // 來回反彈
            empty[Math.Clamp(pos, 0, span - 1)] = '▒';
        }
        var bar = $"[{Theme.Success}]{new string('█', filled)}[/][{Theme.Hairline}]{empty}[/]";
        int pct = (int)Math.Round((double)current / total * 100);
        ConsoleUI.Line($"{bar}  [bold]{current}/{total}[/] [{Theme.TextMuted}]({pct}%)[/]");
        ConsoleUI.Line();

        // 整幀必須塞進視窗高度，否則最後一行的換行會把畫面往上捲、造成 banner 抖動。
        // 精簡 banner(2) + 標題(1) + 空白(1) + 進度條(1) + 空白(1) = 6 行固定開銷；
        // 再保留中斷提示與一行安全邊界。清單最多用剩下的行數。
        int headerRows = 6 + (cancelling ? 1 : 0) + 1;
        // 下限取 1（非 4）：避免在極矮視窗用 Math.Max(4,…) 反而把 maxRows 撐到大於剩餘預算、令整幀超出 h-1。
        int maxRows = Math.Max(1, ConsoleUI.Height - headerRows);
        RenderItemChecklist(items, tick, done, maxRows);

        if (cancelling)
            ConsoleUI.Line($"[{Theme.Warning}]正在中斷…[/]");
        ConsoleUI.EndFrame();
    }

    /// <summary>
    /// 畫出待產生項目清單：依群組分段，逐項以狀態著色——
    /// 未處理（灰）、處理中（橘＋轉圈）、完成（綠勾＋刪除線）、略過/警告（黃）。
    /// 列數受 <paramref name="maxRows"/> 上限約束（含群組標題與省略提示），放不下時以焦點為中心開窗。
    /// </summary>
    private static void RenderItemChecklist(IReadOnlyList<ItemSnap> items, int tick, bool done, int maxRows)
    {
        if (items.Count == 0 || maxRows <= 0) return;

        // 各群組的完成/總數，供群組標題顯示進度。
        var groupTotal = new Dictionary<string, int>();
        var groupDone = new Dictionary<string, int>();
        foreach (var it in items)
        {
            groupTotal[it.Group] = groupTotal.GetValueOrDefault(it.Group) + 1;
            if (it.State is ExportItemState.Done or ExportItemState.Skipped)
                groupDone[it.Group] = groupDone.GetValueOrDefault(it.Group) + 1;
        }

        // 展平成顯示列（群組標題＋各項目），記錄目前焦點列（處理中；無則第一個未處理）。
        // 每列同時帶「可見寬度」與「右側耗時字串」，供 emit 時把計時右對齊到視窗右緣。
        var rows = new List<(string Markup, int PlainWidth, string? Time)>();
        int focus = -1;
        string? curGroup = null;
        string spin = SpinnerFrames[tick % SpinnerFrames.Length];
        foreach (var it in items)
        {
            if (it.Group != curGroup)
            {
                curGroup = it.Group;
                rows.Add(($"[{Theme.Accent}]▌[/] [bold {Theme.Accent}]{Markup.Escape(it.Group)}[/] " +
                          $"[{Theme.TextFaint}]({groupDone.GetValueOrDefault(it.Group)}/{groupTotal[it.Group]})[/]", 0, null));
            }

            // 預留右側計時欄（約 8 欄）：標籤上限較一般窄，避免長物件名把計時擠出畫面。
            var plainLabel = ConsoleUI.Truncate(it.Label, Math.Max(10, ConsoleUI.Width - 18));
            var label = ConsoleUI.Esc(plainLabel);
            string markup;
            string plainSuffix;
            switch (it.State)
            {
                case ExportItemState.Done when it.Warned:
                    markup = $"  [{Theme.Warning}]✓[/] [strikethrough {Theme.Warning}]{label}[/] [{Theme.Warning}](警告)[/]";
                    plainSuffix = " (警告)"; break;
                case ExportItemState.Done:
                    markup = $"  [{Theme.Success}]✓[/] [strikethrough {Theme.TextMuted}]{label}[/]";
                    plainSuffix = ""; break;
                case ExportItemState.Skipped:
                    markup = $"  [{Theme.Warning}]⤼[/] [strikethrough {Theme.TextMuted}]{label}[/] [{Theme.Warning}](略過)[/]";
                    plainSuffix = " (略過)"; break;
                case ExportItemState.Running:
                    markup = $"  [{Theme.Accent}]{spin}[/] [bold]{label}[/]";
                    plainSuffix = ""; break;
                default:
                    markup = $"  [{Theme.TextFaint}]○ {label}[/]";
                    plainSuffix = ""; break;
            }
            // 相依自動補入（非勾選）的項目加標記，方便辨識。
            if (it.IsDependency)
            {
                markup += $" [{Theme.TextFaint}]·相依[/]";
                plainSuffix += " ·相依";
            }
            // 可見寬度＝縮排(2)＋圖示與空白(2)＋標籤＋後綴。
            int plainW = 4 + ConsoleUI.DisplayWidth(plainLabel) + ConsoleUI.DisplayWidth(plainSuffix);
            if (it.State == ExportItemState.Running && focus < 0) focus = rows.Count;
            rows.Add((markup, plainW, ItemTime(it)));
        }
        if (focus < 0)
        {
            // 沒有處理中項目：完成時聚焦尾端，否則聚焦第一個未處理者附近。
            focus = done ? rows.Count - 1 : 0;
        }

        // 把一列輸出：有計時則右對齊到視窗右緣（總寬固定，右緣不抖動）。
        void Emit((string Markup, int PlainWidth, string? Time) r)
        {
            if (r.Time is null) { ConsoleUI.Line(r.Markup); return; }
            int pad = ConsoleUI.Width - r.PlainWidth - ConsoleUI.DisplayWidth(r.Time) - 1;
            if (pad < 1) pad = 1;
            ConsoleUI.Line($"{r.Markup}{new string(' ', pad)}[{Theme.TextFaint}]{r.Time}[/]");
        }

        // 全部塞得下：直接畫，最多 maxRows 列。
        if (rows.Count <= maxRows)
        {
            for (int i = 0; i < rows.Count; i++) Emit(rows[i]);
            return;
        }

        // 放不下：以 focus 為中心開窗，前後各保留一列「…」省略提示（也計入 maxRows）。
        // 先假設上下都有省略列（各佔 1），求出窗內可畫的項目列數，再依實際是否觸頂/觸底回收省略列。
        int windowRows = Math.Max(1, maxRows - 2);
        int start = Math.Clamp(focus - windowRows / 2, 0, rows.Count - windowRows);
        bool hasTop = start > 0;
        bool hasBottom = start + windowRows < rows.Count;
        // 觸頂或觸底時該側不需省略列，把那一列還給窗內項目。
        windowRows = maxRows - (hasTop ? 1 : 0) - (hasBottom ? 1 : 0);
        start = Math.Clamp(focus - windowRows / 2, 0, rows.Count - windowRows);
        hasTop = start > 0;
        hasBottom = start + windowRows < rows.Count;
        int end = start + windowRows;

        if (hasTop) ConsoleUI.Line($"  [{Theme.TextFaint}]…（前 {start} 列略）[/]");
        for (int i = start; i < end; i++) Emit(rows[i]);
        if (hasBottom) ConsoleUI.Line($"  [{Theme.TextFaint}]…（後 {rows.Count - end} 列略）[/]");
    }

    /// <summary>本項目右側要顯示的耗時字串：執行中由起算時刻即時推算（會隨幀增加），完成則為凍結耗時；尚未開始為 null。</summary>
    private static string? ItemTime(ItemSnap it)
    {
        if (it.State == ExportItemState.Running && it.StartedAtUtc is { } s)
        {
            var e = DateTime.UtcNow - s;
            return FormatElapsed(e < TimeSpan.Zero ? TimeSpan.Zero : e);
        }
        return it.Elapsed is { } el ? FormatElapsed(el) : null;
    }

    /// <summary>耗時格式：未滿一分鐘顯示「12.3s」，超過顯示「1m05s」。</summary>
    private static string FormatElapsed(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0.0}s" : $"{(int)t.TotalMinutes}m{t.Seconds:00}s";

    private static void ShowProfileSummary(Profile profile)
    {
        var co = profile.CompareOptions;
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow($"[{Theme.TextMuted}]Profile[/]", $"[bold]{Markup.Escape(profile.Name)}[/]");
        grid.AddRow($"[{Theme.TextMuted}]來源（更版）[/]", Markup.Escape(profile.Source.ToSafeDisplay()));
        grid.AddRow($"[{Theme.TextMuted}]目標（原版）[/]", Markup.Escape(profile.Target.ToSafeDisplay()));
        var deployDb = profile.ResolveDeployDatabaseName();
        var deployDbNote = string.IsNullOrWhiteSpace(profile.ExportOptions.DeployDatabaseName)
            ? $"[{Theme.TextMuted}](沿用目標庫名)[/]" : $"[{Theme.Warning}](已覆寫)[/]";
        grid.AddRow($"[{Theme.TextMuted}]部署 USE 資料庫[/]", $"[bold]{Markup.Escape(deployDb)}[/] {deployDbNote}");
        grid.AddRow($"[{Theme.TextMuted}]忽略權限[/]", co.IgnorePermissions
            ? $"[{Theme.Success}]是（不動 GRANT/DENY/REVOKE）[/]"
            : $"[{Theme.Danger}]否（注意誤刪權限風險）[/]");
        grid.AddRow($"[{Theme.TextMuted}]刪除目標多出物件[/]", co.DropObjectsNotInSource ? $"[{Theme.Danger}]是[/]" : $"[{Theme.Success}]否[/]");
        grid.AddRow($"[{Theme.TextMuted}]資料遺失阻擋[/]", co.BlockOnPossibleDataLoss ? $"[{Theme.Success}]是[/]" : $"[{Theme.Warning}]否[/]");
        grid.AddRow($"[{Theme.TextMuted}]比對描述(MS_Description)[/]", co.IgnoreExtendedProperties ? $"[{Theme.Warning}]否[/]" : $"[{Theme.Success}]是[/]");
        grid.AddRow($"[{Theme.TextMuted}]輸出項目[/]", $"[{Theme.Warning}]{Markup.Escape(profile.ExportOptions.Describe())}[/]");
        AnsiConsole.Write(new Panel(grid)
        {
            Header = new PanelHeader(" 比對情境 "),
            Border = BoxBorder.Rounded,
        }.BorderColor(Theme.Accent.ToSpectre()));
        // 不墊尾端空白：唯一呼叫點後接 AnsiConsole.Status() 互動式 spinner，它會自行在上方墊一行，
        // 否則情境面板與 spinner 之間會出現兩列空白（見 Banner.Compact 的 trailingBlank 說明）。
    }

    private static void ShowDiffOverview(CompareSession session)
    {
        int add = session.Differences.Count(d => d.Kind == ChangeKind.Add);
        int chg = session.Differences.Count(d => d.Kind == ChangeKind.Change);
        int del = session.Differences.Count(d => d.Kind == ChangeKind.Delete);
        AnsiConsole.MarkupLineInterpolated(
            $"找到 [bold]{session.Differences.Count}[/] 項差異：[{Theme.DiffAdd}]+{add} 新增[/]　[{Theme.Warning}]~{chg} 變更[/]　[{Theme.DiffDelete}]-{del} 刪除[/]");
        AnsiConsole.WriteLine();
    }

    private static void ShowExportSummary(ExportSummary summary)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[{Theme.Success}]匯出完成[/]")
        {
            Justification = Justify.Left,
            Style = new Style(foreground: Theme.Hairline.ToSpectre()),
        });

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Theme.TextFaint.ToSpectre());
        table.AddColumn("項目");
        table.AddColumn("結果");
        if (summary.FullScriptPath is not null)
            table.AddRow("完整部署腳本", Markup.Escape(summary.FullScriptPath));
        if (summary.ReverseScriptPath is not null)
            table.AddRow("完整還原腳本", Markup.Escape(summary.ReverseScriptPath));
        if (summary.ObjectScriptCount > 0)
        {
            var v = summary.ObjectScriptVerificationPassed
                ? $"[{Theme.Success}]整理驗證通過[/]"
                : $"[{Theme.Danger}]驗證未過：{Markup.Escape(summary.ObjectScriptVerificationMessage ?? "")}[/]";
            table.AddRow("逐物件部署檔", $"{summary.ObjectScriptCount} 個　{v}");
        }
        if (summary.HtmlReportCount > 0)
            table.AddRow("差異 HTML", $"{summary.HtmlReportCount} 份 + 總覽");
        table.AddRow("輸出目錄", Markup.Escape(summary.OutputDir));
        AnsiConsole.Write(table);

        foreach (var w in summary.Warnings)
            AnsiConsole.MarkupLineInterpolated($"[{Theme.Warning}]! {w}[/]");
    }

    private static string ResolveOutputDir(ConfigStore store, Profile profile, string? overrideDir)
    {
        var dir = overrideDir ?? profile.OutputDir;
        if (Path.IsPathRooted(dir)) return Path.GetFullPath(dir);
        var baseDir = store.ProjectConfigPath is not null
            ? Path.GetDirectoryName(store.ProjectConfigPath)!
            : Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(baseDir, dir));
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { AnsiConsole.MarkupLineInterpolated($"[{Theme.Warning}]無法自動開啟：{ex.Message}[/]"); }
    }
}
