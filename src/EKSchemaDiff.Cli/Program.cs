using ConsoleKit.Hosting;
using EKSchemaDiff.Cli;
using EKSchemaDiff.Cli.Commands;
using EKSchemaDiff.Cli.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

return ConsoleHost.Run<HomeCommand>(
    EksdApp.Info,
    args,
    configureServices: services =>
    {
        services.AddSingleton<Banner>();
        services.AddSingleton<ConfigStoreFactory>();
        services.AddSingleton<CompareWorkflow>();
    },
    configureCommands: config =>
    {
        config.AddCommand<CompareCommand>("compare")
            .WithDescription("直接比對兩個資料庫並匯出（不經主選單）");

        config.AddCommand<ConfigCommand>("config")
            .WithDescription("設定頁：調整 profile 的比對與輸出選項");

        config.AddCommand<InitCommand>("init")
            .WithDescription("在目前目錄建立 .eksd.json 範本");

        config.AddCommand<ProfilesCommand>("profiles")
            .WithDescription("列出已發現的 profile");
    });
