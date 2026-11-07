using System.Runtime.InteropServices;

namespace FpsTune.Wpf.Services;

/// <summary>
/// 读系统当前的默认渲染端点，供「输出到」显示与跟随用（参考实现按同一口径取默认输出）。
///
/// 注册表里的 MMDevices 键只记录端点本身和 <c>DeviceState</c>，不记录哪个是默认；
/// 默认端点由音频策略引擎按「控制台 / 多媒体 / 通话」三种角色分别保存，
/// 唯一可靠的只读来源是 Core Audio 的 <c>IMMDeviceEnumerator::GetDefaultAudioEndpoint</c>。
/// 这里只做枚举读取，不激活设备、不写任何配置。
/// </summary>
internal static class AudioEndpointProbe
{
    private const int RenderFlow = 0;   // EDataFlow.eRender
    private const int ConsoleRole = 0;  // ERole.eConsole
    private const int SOk = 0;

    /// <summary>枚举器类 ID（BCDE0395-E52F-467C-8E3D-C4579291692E）。</summary>
    private static readonly Guid EnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>当前默认渲染端点；读不到（无设备 / 调用失败 / 非 Windows 环境）时返回 null。</summary>
    public static AudioOutput? Probe()
    {
        object? instance = null;
        IMMDevice? device = null;
        try
        {
            var type = Type.GetTypeFromCLSID(EnumeratorClassId);
            if (type is null) return null;
            instance = Activator.CreateInstance(type);
            if (instance is not IMMDeviceEnumerator enumerator) return null;
            if (enumerator.GetDefaultAudioEndpoint(RenderFlow, ConsoleRole, out device) != SOk || device is null) return null;
            if (device.GetId(out var raw) != SOk || raw == IntPtr.Zero) return null;
            string? id;
            try { id = Marshal.PtrToStringUni(raw); }
            finally { Marshal.FreeCoTaskMem(raw); }
            var normalized = Normalize(id);
            // 名称交给调用方用注册表里的友好名替换；这里只保证 ID 可用。
            return normalized is null ? null : new AudioOutput(normalized, normalized);
        }
        catch (COMException) { return null; }
        catch (InvalidCastException) { return null; }
        catch (DllNotFoundException) { return null; }
        finally
        {
            if (device is not null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }
    }

    /// <summary>
    /// 端点 ID 归一化：Core Audio 返回 <c>{0.0.0.00000000}.{GUID}</c>，注册表子键是裸 <c>GUID</c>。
    /// 统一成 <c>D</c> 形式才能和 MMDevices 的键对上；解析不出来返回 null。
    /// </summary>
    internal static string? Normalize(string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId)) return null;
        if (Guid.TryParse(endpointId, out var plain)) return plain.ToString("D");
        // 形如 {0.0.0.00000000}.{GUID}：GUID 在最后一个点之后。
        var dot = endpointId.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < endpointId.Length && Guid.TryParse(endpointId[(dot + 1)..], out var tail))
            return tail.ToString("D");
        // 形如 {0.0.0.00000000}.GUID（带/不带大括号都试一遍）。
        var open = endpointId.LastIndexOf('{');
        var close = endpointId.LastIndexOf('}');
        return open >= 0 && close > open + 1 && Guid.TryParse(endpointId[(open + 1)..close], out var inner)
            ? inner.ToString("D") : null;
    }

    // 只为占住 vtable 槽位：GetDefaultAudioEndpoint 在 EnumAudioEndpoints 之后。
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    // 同上：GetId 在 Activate / OpenPropertyStore 之后。
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, int classContext, IntPtr activationParams, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out int state);
    }
}
