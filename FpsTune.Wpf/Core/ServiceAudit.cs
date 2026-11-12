namespace FpsTune.Wpf.Core;

/// <summary>后台服务按用途分组，界面按组排列。</summary>
internal enum ServiceGroupKind
{
    Xbox,
    Diagnostics,
    Index,
    Sharing,
    Component
}

/// <summary>我们对这个服务的建议。Keep 也要列出来：用户需要知道哪些不能动。</summary>
internal enum ServiceAdvice
{
    /// <summary>用不到就可以关，关掉对常规游戏与桌面使用没有影响。</summary>
    RecommendClose,

    /// <summary>视用法而定：关掉有明确代价，是否接受由用户判断。</summary>
    Optional,

    /// <summary>不要关：关掉会直接影响声音、打印、安全或系统界面。</summary>
    Keep
}

/// <summary>
/// 一条服务审查定义。只描述「这是什么、关掉会怎样、有没有对应的优化项」，
/// 不携带任何系统状态——状态在读取时现取，不在列表里缓存。
/// </summary>
internal sealed record ServiceAuditDefinition(
    string Name,
    string LabelKey,
    ServiceGroupKind Group,
    ServiceAdvice Advice,
    string ReasonKey,
    string? CatalogItemId = null);

/// <summary>读取到的一条服务状态。</summary>
internal sealed record ServiceAuditRow(
    ServiceAuditDefinition Definition,
    ServiceStartType StartType,
    bool Running)
{
    /// <summary>启动类型读不到（无权限/服务不存在）——界面显示「不可用」，不猜。</summary>
    internal bool Readable => StartType != ServiceStartType.Unknown;

    internal bool Disabled => StartType == ServiceStartType.Disabled;

    /// <summary>当前状态是否已经符合建议：建议关的已禁用、建议保留的没被禁用。</summary>
    internal bool MatchesAdvice => Definition.Advice switch
    {
        ServiceAdvice.RecommendClose => Disabled,
        ServiceAdvice.Optional => true,
        _ => !Disabled
    };

    /// <summary>有对应优化项、状态读得到、且当前还没被禁用——只有这种行可以加入优化审阅。</summary>
    internal bool CanReview => Definition.CatalogItemId is { Length: > 0 } && Readable && !Disabled;
}

/// <summary>
/// 服务审查清单（我们自己的内容）：只挑与游戏/日常使用相关、且建议明确的服务。
/// 清单不追求全，宁可少列几条也不要给出叫不准的建议；系统里其它服务不在本页出现。
/// </summary>
internal static class ServiceAuditCatalog
{
    internal static readonly IReadOnlyList<ServiceAuditDefinition> All =
    [
        // Xbox：不用 Xbox 应用与 Game Pass 的机器上常年空转。
        new("XblAuthManager", "Str.ServiceLabelXblAuthManager", ServiceGroupKind.Xbox, ServiceAdvice.RecommendClose, "Str.ServiceWhyXblAuthManager"),
        new("XblGameSave", "Str.ServiceLabelXblGameSave", ServiceGroupKind.Xbox, ServiceAdvice.RecommendClose, "Str.ServiceWhyXblGameSave"),
        new("XboxNetApiSvc", "Str.ServiceLabelXboxNetApiSvc", ServiceGroupKind.Xbox, ServiceAdvice.RecommendClose, "Str.ServiceWhyXboxNetApiSvc"),
        new("XboxGipSvc", "Str.ServiceLabelXboxGipSvc", ServiceGroupKind.Xbox, ServiceAdvice.Optional, "Str.ServiceWhyXboxGipSvc"),

        // 诊断与通知。
        new("DiagTrack", "Str.ServiceLabelDiagTrack", ServiceGroupKind.Diagnostics, ServiceAdvice.RecommendClose, "Str.ServiceWhyDiagTrack"),
        new("WerSvc", "Str.ServiceLabelWerSvc", ServiceGroupKind.Diagnostics, ServiceAdvice.Optional, "Str.ServiceWhyWerSvc"),
        new("WpnService", "Str.ServiceLabelWpnService", ServiceGroupKind.Diagnostics, ServiceAdvice.Keep, "Str.ServiceWhyWpnService"),

        // 索引与预取：这两条已经有同名优化项，可以直接带去审阅。
        new("SysMain", "Str.ServiceLabelSysMain", ServiceGroupKind.Index, ServiceAdvice.Optional, "Str.ServiceWhySysMain", CatalogItemId: "sysmain-off"),
        new("WSearch", "Str.ServiceLabelWSearch", ServiceGroupKind.Index, ServiceAdvice.Optional, "Str.ServiceWhyWSearch", CatalogItemId: "wsearch-off"),

        // 共享与远程：游戏机上基本用不到。
        new("MapsBroker", "Str.ServiceLabelMapsBroker", ServiceGroupKind.Sharing, ServiceAdvice.RecommendClose, "Str.ServiceWhyMapsBroker"),
        new("WMPNetworkSvc", "Str.ServiceLabelWmpNetworkSvc", ServiceGroupKind.Sharing, ServiceAdvice.RecommendClose, "Str.ServiceWhyWmpNetworkSvc"),
        new("Fax", "Str.ServiceLabelFax", ServiceGroupKind.Sharing, ServiceAdvice.RecommendClose, "Str.ServiceWhyFax"),
        new("RemoteRegistry", "Str.ServiceLabelRemoteRegistry", ServiceGroupKind.Sharing, ServiceAdvice.RecommendClose, "Str.ServiceWhyRemoteRegistry"),

        // 系统组件：这几条是「别关」的示范，关了会立刻出问题。
        new("Audiosrv", "Str.ServiceLabelAudiosrv", ServiceGroupKind.Component, ServiceAdvice.Keep, "Str.ServiceWhyAudiosrv"),
        new("Spooler", "Str.ServiceLabelSpooler", ServiceGroupKind.Component, ServiceAdvice.Optional, "Str.ServiceWhySpooler"),
        new("Themes", "Str.ServiceLabelThemes", ServiceGroupKind.Component, ServiceAdvice.Keep, "Str.ServiceWhyThemes"),
        new("WinDefend", "Str.ServiceLabelWinDefend", ServiceGroupKind.Component, ServiceAdvice.Keep, "Str.ServiceWhyWinDefend")
    ];

    /// <summary>组顺序固定：先「能关的」，再「别动的」。</summary>
    internal static readonly ServiceGroupKind[] GroupOrder =
    [
        ServiceGroupKind.Xbox,
        ServiceGroupKind.Diagnostics,
        ServiceGroupKind.Index,
        ServiceGroupKind.Sharing,
        ServiceGroupKind.Component
    ];

    internal static string GroupLabelKey(ServiceGroupKind group) => group switch
    {
        ServiceGroupKind.Xbox => "Str.ServiceGroupXbox",
        ServiceGroupKind.Diagnostics => "Str.ServiceGroupDiagnostics",
        ServiceGroupKind.Index => "Str.ServiceGroupIndex",
        ServiceGroupKind.Sharing => "Str.ServiceGroupSharing",
        _ => "Str.ServiceGroupComponent"
    };

    /// <summary>建议文案（用词直给：可关 / 视情况 / 别关）。</summary>
    internal static string AdviceLabelKey(ServiceAdvice advice) => advice switch
    {
        ServiceAdvice.RecommendClose => "Str.ServiceAdviceClose",
        ServiceAdvice.Optional => "Str.ServiceAdviceOptional",
        _ => "Str.ServiceAdviceKeep"
    };

    /// <summary>
    /// 启动类型的展示文案：直接沿用检测页已有的那套键，界面上两处说法必须一致。
    /// </summary>
    internal static string StartTypeLabelKey(ServiceStartType startType) => startType switch
    {
        ServiceStartType.Auto => "Str.ServiceStartAuto",
        ServiceStartType.Demand => "Str.ServiceStartDemand",
        ServiceStartType.Disabled => "Str.ServiceStartDisabled",
        ServiceStartType.Boot => "Str.ServiceStartBoot",
        ServiceStartType.System => "Str.ServiceStartSystem",
        _ => "Str.Unavailable"
    };

    /// <summary>
    /// 按组汇总当前清单：读取失败的服务仍然占一行（Readable=false），
    /// 让用户看到「这条读不到」而不是悄悄少一行。
    /// </summary>
    internal static IReadOnlyList<ServiceAuditRow> Evaluate(
        Func<string, ServiceStartType> startTypeOf,
        Func<string, string?> stateOf)
    {
        var rows = new List<ServiceAuditRow>(All.Count);
        foreach (var definition in All)
        {
            ServiceStartType startType;
            try { startType = startTypeOf(definition.Name); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                startType = ServiceStartType.Unknown;
            }

            var running = false;
            if (startType != ServiceStartType.Unknown)
            {
                try { running = string.Equals(stateOf(definition.Name), "RUNNING", StringComparison.OrdinalIgnoreCase); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { running = false; }
            }

            rows.Add(new ServiceAuditRow(definition, startType, running));
        }

        return rows;
    }
}
