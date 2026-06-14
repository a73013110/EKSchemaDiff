using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace ConsoleKit.Hosting;

/// <summary>
/// 橋接 Spectre.Console.Cli 的 <see cref="ITypeRegistrar"/> 與 Microsoft.Extensions.DependencyInjection。
/// 擁有唯一的 ServiceProvider：只建立一次（快取），重複 Build() 回傳同一 resolver；負責 Dispose Provider。
///
/// 生命週期規則：Host 不得提前呼叫 Build()。正常流程下 Spectre 會先完成所有註冊
/// （IConfiguration/IAnsiConsole/commands…）再呼叫一次 Build()。一旦 Build 完成，
/// 再呼叫 Register/RegisterInstance/RegisterLazy 會立即丟例外，避免新增註冊被已快取的 Provider 靜默忽略。
/// </summary>
public sealed class ServiceCollectionTypeRegistrar : ITypeRegistrar, IDisposable
{
    private readonly IServiceCollection _services;
    private ServiceProvider? _provider;

    public ServiceCollectionTypeRegistrar(IServiceCollection services) => _services = services;

    public void Register(Type service, Type implementation)
    {
        EnsureNotBuilt();
        _services.AddSingleton(service, implementation);
    }

    public void RegisterInstance(Type service, object implementation)
    {
        EnsureNotBuilt();
        _services.AddSingleton(service, implementation);
    }

    public void RegisterLazy(Type service, Func<object> factory)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(factory);
        _services.AddSingleton(service, _ => factory());
    }

    public ITypeResolver Build()
    {
        _provider ??= _services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        return new Resolver(_provider);
    }

    public void Dispose() => _provider?.Dispose();

    private void EnsureNotBuilt()
    {
        if (_provider is not null)
            throw new InvalidOperationException(
                "ServiceProvider 已建立，無法再新增服務註冊（請在 Build 前完成所有註冊）。");
    }

    /// <summary>輕量 resolver：只解析，不擁有 Provider（Provider 由 Registrar 負責 Dispose）。</summary>
    private sealed class Resolver : ITypeResolver
    {
        private readonly IServiceProvider _provider;
        public Resolver(IServiceProvider provider) => _provider = provider;
        public object? Resolve(Type? type) => type is null ? null : _provider.GetService(type);
    }
}
