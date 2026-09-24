using System.Runtime.InteropServices;

namespace FpsTune.Wpf.Services;

/// <summary>
/// NVAPI 引导（bootstrapping）的唯一实现：加载 nvapi64.dll → 取 <c>nvapi_QueryInterface</c>
/// → 按接口 ID 解析出各接口的函数指针。
///
/// **为什么单独抽出来**：DRS（<see cref="NvidiaDrs"/>）与数字振动
/// （<see cref="DigitalVibranceService"/>）本来各写了一遍引导，而且两套实现**已经分歧**——
/// 一处用 <c>LoadLibrary</c>/<c>GetProcAddress</c>，另一处用 <c>NativeLibrary.Load</c>/<c>GetExport</c>；
/// <c>QueryInterfaceDelegate</c> / <c>InitializeDelegate</c> / <c>IdInitialize</c> / <c>StatusOk</c>
/// 也各声明一份。两份分歧实现是 bug 的温床（改一处忘一处），故集中到此处。
///
/// 本类**不做缓存、不持有状态**：DLL 句柄交给 Windows 的引用计数，按进程存活；
/// 调用方各自持有 <see cref="QueryInterfaceDelegate"/> 并解析自己需要的接口。
/// </summary>
internal static class NvapiNative
{
    /// <summary>NvAPI_Initialize 的接口 ID（官方 nvapi_interface.h）。</summary>
    internal const uint IdInitialize = 0x0150e828;

    /// <summary>NVAPI 状态码：成功。</summary>
    internal const int StatusOk = 0;

    /// <summary><c>nvapi_QueryInterface</c> 导出：按接口 ID 取函数指针；取不到返回 <see cref="IntPtr.Zero"/>。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate IntPtr QueryInterfaceDelegate(uint id);

    /// <summary><c>NvAPI_Initialize</c>：使用任何其它接口前先调用一次。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int InitializeDelegate();

    /// <summary>
    /// 加载 NVAPI 并取到 QueryInterface。非 N 卡 / 无驱动环境返回 <c>false</c>，
    /// 并在 <paramref name="error"/> 给出中文原因（**不抛异常**，调用方据此填 LastError）。
    /// </summary>
    internal static bool TryLoad(out QueryInterfaceDelegate query, out string error)
    {
        query = null!;
        var name = IntPtr.Size == 4 ? "nvapi.dll" : "nvapi64.dll";
        try
        {
            var dll = NativeLibrary.Load(name);
            var export = NativeLibrary.GetExport(dll, "nvapi_QueryInterface");
            query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(export);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = $"未找到 NVIDIA 驱动库（{name}），本机可能不是 NVIDIA 显卡。{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 解析一个接口。<paramref name="fallbackId"/> 非空时，主 ID 取不到就回退到旧 ID
    /// —— DRS 的 Set/Get/DeleteSetting 在不同驱动版本上 ID 不同（官方与社区在用的两套）。
    /// </summary>
    internal static T Resolve<T>(this QueryInterfaceDelegate query, uint id, uint? fallbackId = null)
        where T : Delegate
    {
        var ptr = query(id);
        if (ptr == IntPtr.Zero && fallbackId is { } fallback)
            ptr = query(fallback);
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException($"NVAPI 接口 {id:X8} 不可用");
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }
}
