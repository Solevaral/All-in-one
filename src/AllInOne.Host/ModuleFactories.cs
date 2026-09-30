using AllInOne.Core.Modules;
using AllInOne.Modules.ShutdownTimer;
using AllInOne.Modules.TgWsProxy;
using AllInOne.Modules.Zapret;

namespace AllInOne.Host;

/// <summary>Встроенные в каркас виды модулей. Внешние программы (kind = external) фабрик не требуют.</summary>
internal static class ModuleFactories
{
    public static IEnumerable<IModuleFactory> All() =>
    [
        new ZapretFactory(),
        new TgWsProxyFactory(),
        new ShutdownTimerFactory(),
    ];
}
