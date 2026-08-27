// we-music-ctl —— 壁纸音乐控制
//
// 功能:
//   1. 点击屏幕 (1850, 450) 附近 50px 区域 -> 切换 壁纸音乐 静音/取消静音
//   2. 系统托盘图标: 双击切换音乐; 右键弹出深色圆角菜单(切换音乐 / 退出)
//   3. 操作后有深色圆角小提示反馈
//
// 原理: 通过 Core Audio API 只对 Wallpaper Engine 的音频会话做静音,
//       壁纸动画不受影响。零第三方依赖, 使用系统自带 csc.exe 编译。
//
// 命令行(无 UI): we-music-ctl --toggle | --mute | --unmute | --status

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SysDraw = System.Drawing;
using WinForms = System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }
        if (args.Length > 0) { RunCli(args); return; }

        bool createdNew;
        using (var mtx = new Mutex(true, "WEMusicCtl.SingleInstance", out createdNew))
        {
            if (!createdNew) return;
            RunTray();
        }
    }

    private static void RunTray()
    {
        var app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var tray = new TrayController();
        tray.Start();
        app.Run();
        tray.Cleanup();
    }

    private static void RunCli(string[] args)
    {
        AttachConsole(-1); // 输出到父控制台
        try
        {
            string cmd = (args[0] ?? "").ToLowerInvariant();
            switch (cmd)
            {
                case "--toggle":
                    Console.WriteLine(FormatState(AudioController.Toggle(false))); // 同步, 渐变完成再返回
                    break;
                case "--mute":
                    AudioController.SetAll(true);
                    Console.WriteLine("muted");
                    break;
                case "--unmute":
                    AudioController.SetAll(false);
                    Console.WriteLine("unmuted");
                    break;
                case "--status":
                    Console.WriteLine(FormatState(AudioController.GetState()));
                    break;
                case "--list":
                    foreach (string line in AudioController.EnumerateAllSessions())
                        Console.WriteLine(line);
                    break;
                case "--devices":
                    foreach (string line in AudioController.DiagDevices())
                        Console.WriteLine(line);
                    break;
                case "--all":
                    foreach (string line in AudioController.EnumerateAllDeviceSessions())
                        Console.WriteLine(line);
                    break;
                case "--fakepid":
                    foreach (string line in AudioController.DiagFakePid())
                        Console.WriteLine(line);
                    break;
                case "--muteall":
                    AudioController.MuteAllSessions(true);
                    Console.WriteLine("muted all");
                    break;
                case "--identify":
                    AudioController.IdentifySessions();
                    break;
                case "--pick":
                    RunPick();
                    break;
                case "--scale":
                    Console.WriteLine("scale=" + Ui.Scale.ToString("0.##") + " dpi=" + (Ui.Scale * 96).ToString("0"));
                    break;
                case "--unmuteall":
                    AudioController.MuteAllSessions(false);
                    Console.WriteLine("unmuted all");
                    break;
                default:
                    Console.WriteLine("用法: we-music-ctl [--toggle|--mute|--unmute|--status|--list]");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("error: " + ex.Message);
        }
        finally
        {
            FreeConsole();
        }
    }

    private static string FormatState(bool? state)
    {
        if (state == null) return "not-found";
        return state.Value ? "muted" : "unmuted";
    }

    /// <summary>交互式校准: 点击桌面任意位置, 记录音乐开关触发区域(物理坐标)</summary>
    private static void RunPick()
    {
        var app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var picker = new RegionPicker();
        picker.Start();
        app.Run();
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
}

// ---------------------------------------------------------------------------
// DPI 缩放 / 触发区域配置 / 交互校准
// ---------------------------------------------------------------------------
internal static class Ui
{
    private static double _scale;

    /// <summary>系统 DPI 缩放系数(物理像素 / 逻辑像素)。200% 缩放时 = 2.0</summary>
    public static double Scale
    {
        get
        {
            if (_scale <= 0)
            {
                try { using (var g = SysDraw.Graphics.FromHwnd(IntPtr.Zero)) { _scale = g.DpiX / 96.0; } }
                catch { }
                if (_scale <= 0) _scale = 1.0;
            }
            return _scale;
        }
    }

}

/// <summary>点击触发区域的持久化(物理坐标, 存于 exe 旁的 .region 文件)</summary>
internal static class RegionConfig
{
    private static string FilePath
    {
        get
        {
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                return Path.ChangeExtension(exe, ".region");
            }
            catch { return "we-music-ctl.region"; }
        }
    }

    public static void Load(ref int x, ref int y, ref int radius)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string[] parts = File.ReadAllText(FilePath).Trim().Split(',');
                if (parts.Length >= 2)
                {
                    x = int.Parse(parts[0].Trim());
                    y = int.Parse(parts[1].Trim());
                    if (parts.Length >= 3) radius = int.Parse(parts[2].Trim());
                }
            }
        }
        catch { }
    }

    public static void Save(int x, int y, int radius)
    {
        try { File.WriteAllText(FilePath, x + "," + y + "," + radius); }
        catch { }
    }
}

/// <summary>交互校准: 点一下桌面, 记录触发区域</summary>
internal class RegionPicker
{
    private MouseHook _hook;
    private Window _window;
    private bool _done;

    public void Start()
    {
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = true,
            ResizeMode = ResizeMode.NoResize,
            Width = 520,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var border = new Border
        {
            Background = PopupMenu.Brush(242, 22, 23, 26),
            CornerRadius = new CornerRadius(12),
            BorderBrush = PopupMenu.Brush(44, 255, 255, 255),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 14, 20, 14),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.4, Color = Colors.Black },
        };
        border.Child = new TextBlock
        {
            Text = "点击桌面任意位置，设为音乐开关的触发区域\n(按 Esc 取消)",
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 15,
            Foreground = PopupMenu.Brush(236, 236, 238, 240),
            TextAlignment = TextAlignment.Center,
            LineHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _window.Content = border;
        _window.PreviewKeyDown += OnKey;
        _window.Show();
        _window.Activate();

        _hook = new MouseHook();
        _hook.LeftButtonDown += OnClick;
        _hook.Install();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Quit();
    }

    private void OnClick(int x, int y)
    {
        if (_done) return;
        double s = Ui.Scale;
        var r = new Rect(_window.Left * s, _window.Top * s,
            _window.ActualWidth * s, _window.ActualHeight * s);
        if (r.Contains((double)x, (double)y)) return; // 点到了提示窗上, 忽略
        _done = true;
        RegionConfig.Save(x, y, 50);
        Console.WriteLine("region set: " + x + "," + y + ",50");
        Quit();
    }

    private void Quit()
    {
        if (_hook != null) _hook.Uninstall();
        if (_window != null) _window.Close();
        Application.Current.Shutdown();
    }
}

// ---------------------------------------------------------------------------
// 音频控制: 枚举所有音频会话, 找到 Wallpaper Engine 的进程并控制静音
// ---------------------------------------------------------------------------
internal static class AudioController
{
    private const string ProcessMatch = "wallpaper";
    private static readonly Guid SessionManager2Iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private const int ClsCtxAll = 22; // CLSCTX_ALL

    private static readonly Guid SessionControlIid = new Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD");
    private static readonly Guid SessionControl2Iid = new Guid("BFB3FF50-46B4-44A5-9ABD-831733D6D57A");
    private static readonly Guid SessionVolumeIid = new Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    /// <summary>
    /// 识别壁纸音乐会话并返回其音量接口。
    /// 本系统的会话拿不到进程ID(无 IAudioSessionControl2), 改用启发式:
    /// 壁纸进程往往有多个音频流共享同一个分组(GetGroupingParam),
    /// 因此 "分组里会话数>=2" 的分组被视为壁纸音频分组。
    /// </summary>
    public static List<ISimpleAudioVolume> GetWallpaperVolumes()
    {
        var list = new List<ISimpleAudioVolume>();
        IFakeEnum sessionEnum = GetSessionEnumeratorRaw();
        if (sessionEnum == null) return list;
        try
        {
            int count;
            sessionEnum.GetCount(out count);
            var groupCount = new Dictionary<Guid, int>();
            var groupVolumes = new Dictionary<Guid, List<ISimpleAudioVolume>>();
            for (int i = 0; i < count; i++)
            {
                IntPtr pSession;
                if (sessionEnum.GetSession(i, out pSession) != 0 || pSession == IntPtr.Zero) continue;
                Guid group = Guid.Empty;
                int state = 0;
                Guid cIid = SessionControlIid;
                IntPtr pCtl;
                if (Marshal.QueryInterface(pSession, ref cIid, out pCtl) == 0)
                {
                    try
                    {
                        var ctl = (IAudioSessionControl)Marshal.GetTypedObjectForIUnknown(
                            pCtl, typeof(IAudioSessionControl));
                        ctl.GetGroupingParam(out group);
                        ctl.GetState(out state);
                    }
                    catch { }
                    Marshal.Release(pCtl);
                }
                // 只统计活跃(state=1)的会话, 避免不活跃会话干扰开关判断
                if (group != Guid.Empty && state == 1)
                {
                    Guid vIid = SessionVolumeIid;
                    IntPtr pVol;
                    if (Marshal.QueryInterface(pSession, ref vIid, out pVol) == 0)
                    {
                        try
                        {
                            var volume = (ISimpleAudioVolume)Marshal.GetTypedObjectForIUnknown(
                                pVol, typeof(ISimpleAudioVolume));
                            List<ISimpleAudioVolume> vols;
                            if (!groupVolumes.TryGetValue(group, out vols))
                            {
                                vols = new List<ISimpleAudioVolume>();
                                groupVolumes[group] = vols;
                                groupCount[group] = 0;
                            }
                            groupCount[group]++;
                            vols.Add(volume);
                        }
                        catch { }
                        Marshal.Release(pVol);
                    }
                }
                Marshal.Release(pSession);
            }
            foreach (var kv in groupCount)
            {
                if (kv.Value >= 2) // 多会程共享分组 = 壁纸音频
                    list.AddRange(groupVolumes[kv.Key]);
            }
        }
        catch { /* 会话读取异常时返回已收集的部分 */ }
        if (list.Count == 0)
            list = GetActiveNonSystemVolumes(); // 回退: 识别不到时切换所有正在播放的非系统会话, 保证开关可用
        return list;
    }

    /// <summary>回退: 收集所有"正在播放(active)且非系统声音"的会话音量接口</summary>
    private static List<ISimpleAudioVolume> GetActiveNonSystemVolumes()
    {
        var list = new List<ISimpleAudioVolume>();
        IFakeEnum sessionEnum = GetSessionEnumeratorRaw();
        if (sessionEnum == null) return list;
        try
        {
            int count;
            sessionEnum.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                IntPtr pSession;
                if (sessionEnum.GetSession(i, out pSession) != 0 || pSession == IntPtr.Zero) continue;
                bool active = false;
                bool isSystem = false;
                bool muted = false;
                Guid cIid = SessionControlIid;
                IntPtr pCtl;
                if (Marshal.QueryInterface(pSession, ref cIid, out pCtl) == 0)
                {
                    try
                    {
                        var ctl = (IAudioSessionControl)Marshal.GetTypedObjectForIUnknown(
                            pCtl, typeof(IAudioSessionControl));
                        int st;
                        ctl.GetState(out st);
                        active = (st == 1); // AudioSessionStateActive
                        IntPtr pName;
                        ctl.GetDisplayName(out pName);
                        if (pName != IntPtr.Zero)
                        {
                            string nm = Marshal.PtrToStringUni(pName);
                            if (nm != null && nm.IndexOf("AudioSrv", StringComparison.OrdinalIgnoreCase) >= 0)
                                isSystem = true;
                            Marshal.FreeCoTaskMem(pName);
                        }
                    }
                    catch { }
                    Marshal.Release(pCtl);
                }
                if (active && !isSystem)
                {
                    Guid vIid2 = SessionVolumeIid;
                    IntPtr pVol2;
                    if (Marshal.QueryInterface(pSession, ref vIid2, out pVol2) == 0)
                    {
                        try
                        {
                            var volume = (ISimpleAudioVolume)Marshal.GetTypedObjectForIUnknown(
                                pVol2, typeof(ISimpleAudioVolume));
                            list.Add(volume);
                        }
                        catch { }
                        Marshal.Release(pVol2);
                    }
                }
                Marshal.Release(pSession);
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 用 PreserveSig 获取会话枚举器, 再借 IUnknown 假接口按 vtable 访问。
    /// 此系统返回的枚举器对象不支持官方 IAudioSessionEnumerator (B4B8E598),
    /// 但 vtable 完全可用, 故用 IFakeEnum(同 IUnknown GUID) 绕开。
    /// </summary>
    private static IFakeEnum GetSessionEnumeratorRaw()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type);
            IMMDevice device;
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device);
            return GetSessionEnumeratorRaw(device);
        }
        catch { return null; }
    }

    private static IFakeEnum GetSessionEnumeratorRaw(IMMDevice device)
    {
        try
        {
            object managerObj;
            Guid iid = SessionManager2Iid;
            device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out managerObj);
            var raw = (IAudioSessionManager2Raw)managerObj;
            IntPtr pEnum;
            int hr = raw.GetSessionEnumerator(out pEnum);
            if (hr != 0 || pEnum == IntPtr.Zero) return null;
            var result = (IFakeEnum)Marshal.GetObjectForIUnknown(pEnum);
            Marshal.Release(pEnum);
            return result;
        }
        catch { return null; }
    }

    /// <summary>调试用: 在所有渲染设备上枚举会话, 找 WE 音乐会话所在设备</summary>
    public static List<string> EnumerateAllDeviceSessions()
    {
        var lines = new List<string>();
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type);
            string defaultId = "";
            try
            {
                IMMDevice def;
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out def);
                IntPtr pId;
                def.GetId(out pId);
                if (pId != IntPtr.Zero) { defaultId = Marshal.PtrToStringUni(pId); Marshal.FreeCoTaskMem(pId); }
            }
            catch { }

            IMMDeviceCollection coll;
            enumerator.EnumAudioEndpoints(EDataFlow.eRender, 1, out coll);
            uint cnt;
            coll.GetCount(out cnt);
            for (uint i = 0; i < cnt; i++)
            {
                IMMDevice dev;
                coll.Item(i, out dev);
                IntPtr pId;
                dev.GetId(out pId);
                string id = pId == IntPtr.Zero ? "?" : Marshal.PtrToStringUni(pId);
                if (pId != IntPtr.Zero) Marshal.FreeCoTaskMem(pId);
                lines.Add("== device[" + i + "] " + (id == defaultId ? "[DEFAULT] " : "") +
                    GetDeviceFriendlyName(dev) + " " + id);
                DescribeDeviceSessions(lines, dev);
            }
        }
        catch (Exception ex)
        {
            lines.Add("err: " + ex.Message);
        }
        return lines;
    }

    private static void DescribeDeviceSessions(List<string> lines, IMMDevice dev)
    {
        IFakeEnum sessionEnum = GetSessionEnumeratorRaw(dev);
        if (sessionEnum == null) { lines.Add("  (no session enumerator)"); return; }
        try
        {
            int count;
            sessionEnum.GetCount(out count);
            lines.Add("  sessions=" + count);
            for (int i = 0; i < count; i++)
            {
                IntPtr pSession;
                if (sessionEnum.GetSession(i, out pSession) != 0 || pSession == IntPtr.Zero) continue;
                string display = "?", icon = "?", state = "?";
                Guid cIid = SessionControlIid;
                IntPtr pCtl;
                if (Marshal.QueryInterface(pSession, ref cIid, out pCtl) == 0)
                {
                    try
                    {
                        var ctl = (IAudioSessionControl)Marshal.GetTypedObjectForIUnknown(pCtl, typeof(IAudioSessionControl));
                        int st;
                        ctl.GetState(out st);
                        state = st.ToString();
                        IntPtr pName;
                        ctl.GetDisplayName(out pName);
                        if (pName != IntPtr.Zero) { display = Marshal.PtrToStringUni(pName); Marshal.FreeCoTaskMem(pName); }
                        IntPtr pIcon;
                        ctl.GetIconPath(out pIcon);
                        if (pIcon != IntPtr.Zero) { icon = Marshal.PtrToStringUni(pIcon); Marshal.FreeCoTaskMem(pIcon); }
                    }
                    catch { }
                }
                bool c2 = false;
                Guid c2Iid = SessionControl2Iid;
                IntPtr pC2;
                if (Marshal.QueryInterface(pSession, ref c2Iid, out pC2) == 0) { c2 = true; }
                bool muted = false;
                Guid vIid = SessionVolumeIid;
                IntPtr pVol;
                if (Marshal.QueryInterface(pSession, ref vIid, out pVol) == 0)
                {
                    try
                    {
                        var volume = (ISimpleAudioVolume)Marshal.GetTypedObjectForIUnknown(pVol, typeof(ISimpleAudioVolume));
                        volume.GetMute(out muted);
                    }
                    catch { }
                }
                lines.Add("   [" + i + "] state=" + state + " c2=" + (c2 ? "Y" : "N") +
                    " mute=" + (muted ? "1" : "0") + " disp='" + display + "' icon='" + icon + "'");
            }
        }
        catch { }
    }

    /// <summary>调试用: 直接按 vtable 调 GetProcessId(带损坏异常保护, 崩溃可捕获)</summary>
    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    public static List<string> DiagFakePid()
    {
        var lines = new List<string>();
        IFakeEnum sessionEnum = GetSessionEnumeratorRaw();
        if (sessionEnum == null) { lines.Add("no enum"); return lines; }
        try
        {
            int count;
            sessionEnum.GetCount(out count);
            lines.Add("count=" + count);
            for (int i = 0; i < count; i++)
            {
                IntPtr pSession;
                if (sessionEnum.GetSession(i, out pSession) != 0 || pSession == IntPtr.Zero) continue;
                uint pid = 0;
                int hr = -1;
                string err = "-";
                try
                {
                    var fe = (IFakeCtl2)Marshal.GetObjectForIUnknown(pSession);
                    hr = fe.GetProcessId(out pid);
                }
                catch (Exception ex) { err = ex.GetType().Name; }
                string name = "<n/a>";
                if (pid != 0) { try { using (var proc = Process.GetProcessById((int)pid)) name = proc.ProcessName; } catch { } }
                lines.Add("[" + i + "] pid=" + pid + " " + name + " hr=0x" + hr.ToString("X8") + " err=" + err);
            }
        }
        catch (Exception ex)
        {
            lines.Add("err: " + ex.Message);
        }
        return lines;
    }

    /// <summary>调试用: 列出所有活动渲染设备及默认标记、友好名</summary>
    public static List<string> DiagDevices()
    {
        var lines = new List<string>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            string defaultId = "";
            try
            {
                IMMDevice def;
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out def);
                IntPtr pId;
                def.GetId(out pId);
                if (pId != IntPtr.Zero) { defaultId = Marshal.PtrToStringUni(pId); Marshal.FreeCoTaskMem(pId); }
            }
            catch { }

            IMMDeviceCollection coll;
            enumerator.EnumAudioEndpoints(EDataFlow.eRender, 1, out coll); // DEVICE_STATE_ACTIVE
            uint cnt;
            coll.GetCount(out cnt);
            lines.Add("render devices count=" + cnt);
            for (uint i = 0; i < cnt; i++)
            {
                IMMDevice dev;
                coll.Item(i, out dev);
                IntPtr pId;
                dev.GetId(out pId);
                string id = pId == IntPtr.Zero ? "?" : Marshal.PtrToStringUni(pId);
                if (pId != IntPtr.Zero) Marshal.FreeCoTaskMem(pId);
                string name = GetDeviceFriendlyName(dev);
                lines.Add((id == defaultId ? "  [DEFAULT] " : "  ") + name + "  (" + id + ")");
            }
        }
        catch (Exception ex)
        {
            lines.Add("err: " + ex.Message);
        }
        return lines;
    }

    private static string GetDeviceFriendlyName(IMMDevice dev)
    {
        try
        {
            IPropertyStore store;
            dev.OpenPropertyStore(0, out store); // STGM_READ
            var key = new PropertyKey
            {
                fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), // PKEY_Device_FriendlyName
                pid = 14,
            };
            PropVariant value;
            store.GetValue(ref key, out value);
            if (value.vt == 31 && value.pVal != IntPtr.Zero) return Marshal.PtrToStringUni(value.pVal);
            return "(vt=" + value.vt + ")";
        }
        catch { return "(?)"; }
    }

    private static bool IsWallpaperProcess(int pid)
    {
        try
        {
            using (var proc = Process.GetProcessById(pid))
                return proc.ProcessName.IndexOf(ProcessMatch, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    /// <summary>调试用: 列出默认渲染设备上的所有音频会话 (显示名 状态 进程名 静音)</summary>
    public static List<string> EnumerateAllSessions()
    {
        var lines = new List<string>();
        IFakeEnum sessionEnum = GetSessionEnumeratorRaw();
        if (sessionEnum == null) { lines.Add("error: 获取会话枚举器失败"); return lines; }
        try
        {
            int count;
            sessionEnum.GetCount(out count);
            lines.Add("count=" + count);
            for (int i = 0; i < count; i++)
            {
                IntPtr pSession;
                if (sessionEnum.GetSession(i, out pSession) != 0 || pSession == IntPtr.Zero) continue;

                string display = "?";
                string icon = "?";
                string state = "?";
                string group = "?";
                Guid cIid = SessionControlIid;
                IntPtr pCtl;
                if (Marshal.QueryInterface(pSession, ref cIid, out pCtl) == 0)
                {
                    try
                    {
                        var ctl = (IAudioSessionControl)Marshal.GetTypedObjectForIUnknown(
                            pCtl, typeof(IAudioSessionControl));
                        int st;
                        ctl.GetState(out st);
                        state = st.ToString();
                        IntPtr pName;
                        ctl.GetDisplayName(out pName);
                        if (pName != IntPtr.Zero)
                        {
                            display = Marshal.PtrToStringUni(pName);
                            Marshal.FreeCoTaskMem(pName);
                        }
                        IntPtr pIcon;
                        ctl.GetIconPath(out pIcon);
                        if (pIcon != IntPtr.Zero)
                        {
                            icon = Marshal.PtrToStringUni(pIcon);
                            Marshal.FreeCoTaskMem(pIcon);
                        }
                        Guid gp;
                        ctl.GetGroupingParam(out gp);
                        group = gp.ToString();
                    }
                    catch (Exception ex) { display = "ctl-err:" + ex.Message; }
                }

                uint pid = 0;
                bool systemSounds = false;
                string ctl2Err = "-";
                Guid c2Iid = SessionControl2Iid;
                IntPtr pC2;
                if (Marshal.QueryInterface(pSession, ref c2Iid, out pC2) == 0)
                {
                    try
                    {
                        var ctl2 = (IAudioSessionControl2)Marshal.GetTypedObjectForIUnknown(
                            pC2, typeof(IAudioSessionControl2));
                        ctl2.GetProcessId(out pid);
                        ctl2.IsSystemSoundsSession();
                        systemSounds = true;
                    }
                    catch (Exception ex) { ctl2Err = ex.GetType().Name + ": " + ex.Message; }
                }
                // 用假接口按 vtable 直接在 QI 出的 control2 对象上调 GetProcessId
                uint pid2 = 0;
                int hrPid = -1;
                if (pC2 != IntPtr.Zero)
                {
                    try
                    {
                        var fe2 = (IFakeCtl2)Marshal.GetObjectForIUnknown(pC2);
                        hrPid = fe2.GetProcessId(out pid2);
                    }
                    catch (Exception ex) { ctl2Err += " | fake:" + ex.GetType().Name; }
                }

                string name = "<unknown>";
                if (pid != 0) { try { using (var proc = Process.GetProcessById((int)pid)) name = proc.ProcessName; } catch { } }
                bool muted = false;
                Guid vIid = SessionVolumeIid;
                IntPtr pVol;
                if (Marshal.QueryInterface(pSession, ref vIid, out pVol) == 0)
                {
                    try
                    {
                        var volume = (ISimpleAudioVolume)Marshal.GetTypedObjectForIUnknown(
                            pVol, typeof(ISimpleAudioVolume));
                        volume.GetMute(out muted);
                    }
                    catch { }
                }
                lines.Add("[" + i + "] state=" + state + " mute=" + (muted ? "1" : "0") +
                    " disp='" + display + "' icon='" + icon + "' group=" + group + " err=" + ctl2Err);
            }
        }
        catch (Exception ex)
        {
            lines.Add("error: " + ex.Message);
        }
        return lines;
    }

    /// <summary>音乐渐变恢复时的目标音量(静音前记录, 取最大防中间值污染)</summary>
    private static volatile float _restoreVolume = 1f;

    /// <summary>渐变代次: 每次切换递增, 旧渐变检测到代次变化就停止, 让新渐变接管</summary>
    private static int _fadeGen = 0;

    /// <summary>期望状态(muted=true)。用翻转跟踪而非读 GetMute, 避免渐变中判断错乱</summary>
    private static bool _desiredMuted = false;
    private static bool _initialized = false;

    /// <returns>null=未找到会话; true=操作后已静音; false=操作后已取消静音</returns>
    public static bool? Toggle(bool asyncFade)
    {
        var volumes = GetWallpaperVolumes();
        if (volumes.Count == 0) return null;
        if (!_initialized)
        {
            // 首次: 同步期望状态为真实状态, 之后每次翻转
            bool allMuted = true;
            foreach (var v in volumes)
            {
                bool muted;
                v.GetMute(out muted);
                if (!muted) { allMuted = false; break; }
            }
            _desiredMuted = allMuted;
            _restoreVolume = 1f;
            _initialized = true;
        }
        _desiredMuted = !_desiredMuted; // 翻转期望状态(不依赖渐变中的真实 mute)
        int myGen = ++_fadeGen;         // 本次切换代次
        foreach (var v in volumes)
        {
            float current = 1f;
            try { v.GetMasterVolume(out current); } catch { }
            if (_desiredMuted)
            {
                // 关闭: 记录原始音量(取最大, 防中间值污染)
                if (current > 0.1f) _restoreVolume = Math.Max(_restoreVolume, current);
                StartFade(v, current, 0f, true, myGen, asyncFade);   // 从当前音量慢慢降到 0
            }
            else
            {
                // 开启: 立即解除 mute, 从当前音量慢慢升回原始音量
                try { v.SetMute(false, Guid.Empty); } catch { }
                StartFade(v, current, _restoreVolume, false, myGen, asyncFade);
            }
        }
        return _desiredMuted;
    }

    /// <summary>音量渐变(from -> to, 约0.6秒), 结束后 SetMute。
/// 代次机制: 渐变中若 _fadeGen 已变化(有新的切换), 立即停止, 让新切换接管。
/// asyncFade=true 时后台执行(钩子回调必须用异步, 避免超过系统钩子超时被移除)。</summary>
    private static void StartFade(ISimpleAudioVolume vol, float from, float to, bool endMute, int gen, bool asyncFade)
    {
        Action action = delegate
        {
            try
            {
                int steps = 15;
                int delayMs = 40; // 约 0.6 秒
                Guid ctx = Guid.Empty;
                for (int i = 1; i <= steps; i++)
                {
                    if (_fadeGen != gen) return; // 有更新的切换, 停止本次渐变
                    float v = from + (to - from) * i / steps;
                    vol.SetMasterVolume(v, ref ctx);
                    Thread.Sleep(delayMs);
                }
                if (_fadeGen == gen) // 只有无新切换时才设置最终 mute 状态
                    vol.SetMute(endMute, ref ctx);
            }
            catch { }
        };
        if (asyncFade)
            System.Threading.Tasks.Task.Factory.StartNew(action);
        else
            action();
    }

    /// <returns>null=未找到会话; true=全部已静音; false=存在未静音的会话</returns>
    public static bool? GetState()
    {
        var volumes = GetWallpaperVolumes();
        if (volumes.Count == 0) return null;
        bool allMuted = true;
        foreach (var v in volumes)
        {
            bool muted;
            v.GetMute(out muted);
            if (!muted) allMuted = false;
        }
        return allMuted;
    }

    public static void SetAll(bool muted)
    {
        SetMuteAll(GetWallpaperVolumes(), muted);
    }

    /// <summary>调试用: 把每个活跃会话依次静音2秒, 让用户听出哪个是壁纸音乐</summary>
    public static void IdentifySessions()
    {
        MuteAllSessions(false); // 先确保都没静音
        IFakeEnum sessionEnum = GetSessionEnumeratorRaw();
        if (sessionEnum == null) { Console.WriteLine("no enum"); return; }
        int count;
        sessionEnum.GetCount(out count);
        Console.WriteLine("默认设备会话数=" + count);
        for (int i = 0; i < count; i++)
        {
            IntPtr pSession;
            if (sessionEnum.GetSession(i, out pSession) != 0 || pSession == IntPtr.Zero) continue;
            bool active;
            string display;
            SessionBasics(pSession, out active, out display);
            Console.WriteLine("[" + i + "] active=" + (active ? "Y" : "N") + " disp='" + display + "'");
            Marshal.Release(pSession);
        }
        Console.WriteLine("--- 依次静音每个活跃会话 2 秒 ---");
        for (int i = 0; i < count; i++)
        {
            IntPtr pSession;
            if (sessionEnum.GetSession(i, out pSession) != 0 || pSession == IntPtr.Zero) continue;
            bool active;
            string display;
            SessionBasics(pSession, out active, out display);
            if (active)
            {
                Console.WriteLine(">>> 静音会话[" + i + "] 2秒 (disp='" + display + "') — 若音乐停, 这就是壁纸音乐");
                SetSessionMute(pSession, true);
                System.Threading.Thread.Sleep(2000);
                SetSessionMute(pSession, false);
                Console.WriteLine("<<< 恢复会话[" + i + "]");
            }
            Marshal.Release(pSession);
        }
        Console.WriteLine("--- 完成, 请告诉我哪个编号是壁纸音乐 ---");
    }

    private static void SessionBasics(IntPtr pSession, out bool active, out string display)
    {
        active = false;
        display = "?";
        Guid cIid = SessionControlIid;
        IntPtr pCtl;
        if (Marshal.QueryInterface(pSession, ref cIid, out pCtl) == 0)
        {
            try
            {
                var ctl = (IAudioSessionControl)Marshal.GetTypedObjectForIUnknown(pCtl, typeof(IAudioSessionControl));
                int st;
                ctl.GetState(out st);
                active = (st == 1);
                IntPtr pName;
                ctl.GetDisplayName(out pName);
                if (pName != IntPtr.Zero) { display = Marshal.PtrToStringUni(pName); Marshal.FreeCoTaskMem(pName); }
            }
            catch { }
        }
    }

    private static void SetSessionMute(IntPtr pSession, bool muted)
    {
        Guid vIid = SessionVolumeIid;
        IntPtr pVol;
        if (Marshal.QueryInterface(pSession, ref vIid, out pVol) == 0)
        {
            try
            {
                var volume = (ISimpleAudioVolume)Marshal.GetTypedObjectForIUnknown(pVol, typeof(ISimpleAudioVolume));
                volume.SetMute(muted, Guid.Empty);
            }
            catch { }
        }
    }

    /// <summary>静音默认设备上的所有会话(无进程过滤, 用于验证/回退)</summary>
    public static void MuteAllSessions(bool muted)
    {
        IFakeEnum sessionEnum = GetSessionEnumeratorRaw();
        if (sessionEnum == null) return;
        try
        {
            int count;
            sessionEnum.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                IntPtr pSession;
                if (sessionEnum.GetSession(i, out pSession) != 0 || pSession == IntPtr.Zero) continue;
                Guid vIid = SessionVolumeIid;
                IntPtr pVol;
                if (Marshal.QueryInterface(pSession, ref vIid, out pVol) == 0)
                {
                    try
                    {
                        var volume = (ISimpleAudioVolume)Marshal.GetTypedObjectForIUnknown(
                            pVol, typeof(ISimpleAudioVolume));
                        volume.SetMute(muted, Guid.Empty);
                    }
                    catch { }
                }
                Marshal.Release(pSession);
            }
        }
        catch { }
    }

    public static void RestoreUnmuted()
    {
        SetAll(false);
    }

    private static void SetMuteAll(List<ISimpleAudioVolume> volumes, bool muted)
    {
        foreach (var v in volumes)
        {
            try { v.SetMute(muted, Guid.Empty); } catch { }
        }
    }
}

// ---------------------------------------------------------------------------
// Core Audio COM 互操作接口
// ---------------------------------------------------------------------------
internal enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
internal enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(object client);
    void UnregisterEndpointNotificationCallback(object client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void OpenPropertyStore(int access, out IPropertyStore props);
    void GetId(out IntPtr id);
    void GetState(out int state);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    void GetCount(out uint count);
    void Item(uint index, out IMMDevice device);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public uint pid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public IntPtr pVal;   // VT_LPWSTR 时指向字符串
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    void Commit();
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    void GetAudioSessionControl(ref Guid sessionGuid, int streamFlags, out IAudioSessionControl session);
    void GetSimpleAudioVolume(ref Guid sessionGuid, int streamFlags, out ISimpleAudioVolume volume);
    void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    void RegisterSessionNotification(IntPtr notification);
    void UnregisterSessionNotification(IntPtr notification);
    void RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
    void UnregisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId);
}

// 借 IUnknown GUID 的假枚举接口: 本系统的会话枚举器不支持官方
// IAudioSessionEnumerator(B4B8E598), 用同 IUnknown GUID 的接口按 vtable 访问
[ComImport, Guid("00000000-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFakeEnum
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IntPtr session);
}

// 借 IUnknown GUID 的假会话控制2接口(按 vtable 直接调, 用于验证 GetProcessId)
[ComImport, Guid("00000000-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFakeCtl2
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out IntPtr name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
    [PreserveSig] int GetIconPath(out IntPtr path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
    [PreserveSig] int GetGroupingParam(out Guid g);
    [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
    [PreserveSig] int GetSessionIdentifier(out IntPtr id);
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference(bool optIn);
}

// 生产代码实际使用的 PreserveSig 版本(手动包装, 避开 CLR 自动 RCW 的 QI 问题)
[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2Raw
{
    [PreserveSig] int GetAudioSessionControl(ref Guid g, int f, out IntPtr s);
    [PreserveSig] int GetSimpleAudioVolume(ref Guid g, int f, out IntPtr v);
    [PreserveSig] int GetSessionEnumerator(out IntPtr e);
    [PreserveSig] int RegisterSessionNotification(IntPtr n);
    [PreserveSig] int UnregisterSessionNotification(IntPtr n);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr n);
    [PreserveSig] int UnregisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string id);
}

[ComImport, Guid("B4B8E598-6E18-4189-B832-0EB01BFB1950"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    void GetCount(out int count);
    void GetSession(int index, out IAudioSessionControl session);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    void GetState(out int state);
    void GetDisplayName(out IntPtr name);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
    void GetIconPath(out IntPtr path);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
    void GetGroupingParam(out Guid groupingParam);
    void SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
    void RegisterAudioSessionNotification(IntPtr notification);
    void UnregisterAudioSessionNotification(IntPtr notification);
}

[ComImport, Guid("BFB3FF50-46B4-44A5-9ABD-831733D6D57A"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    void GetState(out int state);
    void GetDisplayName(out IntPtr name);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
    void GetIconPath(out IntPtr path);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
    void GetGroupingParam(out Guid groupingParam);
    void SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
    void RegisterAudioSessionNotification(IntPtr notification);
    void UnregisterAudioSessionNotification(IntPtr notification);
    void GetSessionIdentifier(out IntPtr identifier);
    void GetSessionInstanceIdentifier(out IntPtr identifier);
    void GetProcessId(out uint pid);
    void IsSystemSoundsSession();
    void SetDuckingPreference(bool optIn);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, ref Guid eventContext);
    void GetMasterVolume(out float level);
    void SetMute(bool mute, ref Guid eventContext);
    void GetMute(out bool mute);
}

// ---------------------------------------------------------------------------
// 全局低级鼠标钩子: 捕捉屏幕指定区域的左键点击
// ---------------------------------------------------------------------------
internal class MouseHook : IDisposable
{
    private const int WmLeftButtonDown = 0x0201;
    private const int WhMouseLl = 14;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public NativePoint pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelMouseProc _proc;

    public event Action<int, int> LeftButtonDown;

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;
        _proc = OnHookProc;
        IntPtr hMod = IntPtr.Zero;
        try
        {
            using (var proc = Process.GetCurrentProcess())
            using (var module = proc.MainModule)
                hMod = GetModuleHandle(module.ModuleName);
        }
        catch { }
        _hookId = SetWindowsHookEx(WhMouseLl, _proc, hMod, 0);
    }

    private IntPtr OnHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WmLeftButtonDown)
        {
            var data = (MsllHookStruct)Marshal.PtrToStructure(lParam, typeof(MsllHookStruct));
            var handler = LeftButtonDown;
            if (handler != null) handler(data.pt.X, data.pt.Y);
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    public void Dispose() { Uninstall(); }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);
}

// ---------------------------------------------------------------------------
// 托盘控制器: 图标 + 双击/右键 + 全局钩子联动
// ---------------------------------------------------------------------------
internal class TrayController
{
    // 点击区域: (1855, 755) 半径 50px (默认值, 可通过 --pick 校准覆盖)
    private const int ClickX = 1855;
    private const int ClickY = 755;
    private const int ClickRadius = 50;

    private readonly MouseHook _hook = new MouseHook();
    private WinForms.NotifyIcon _tray;
    private SysDraw.Icon _iconOn;
    private SysDraw.Icon _iconOff;
    private bool _trayShowsOff;
    private PopupMenu _menu;

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect iconLocation);

    public void Start()
    {
        _iconOn = LoadIcon("tray-on.ico");
        _iconOff = LoadIcon("tray-off.ico");

        _tray = new WinForms.NotifyIcon();
        bool? muted = AudioController.GetState();
        _trayShowsOff = (muted == true);
        _tray.Icon = _trayShowsOff ? _iconOff : _iconOn;
        _tray.Text = "壁纸音乐控制";
        _tray.Visible = true;
        _tray.DoubleClick += OnTrayDoubleClick;
        _tray.MouseUp += OnTrayMouseUp;

        _hook.LeftButtonDown += OnGlobalMouseDown;
        _hook.Install();
    }

    public void Cleanup()
    {
        _hook.Uninstall();
        AudioController.RestoreUnmuted(); // 退出时恢复音乐, 避免永远静音
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        if (_menu != null) { _menu.Close(); _menu = null; }
    }

    private void OnTrayDoubleClick(object sender, EventArgs e)
    {
        ToggleMusic(-1, -1); // 提示显示在托盘附近
    }

    private void OnTrayMouseUp(object sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Right) ShowMenu();
    }

    private void OnGlobalMouseDown(int x, int y)
    {
        // 已打开的菜单: 点击外部关闭, 点击内部交给菜单处理
        // 注意: 钩子给的是物理坐标, 菜单 Left/Top/Width 是逻辑坐标, 需乘缩放系数
        if (_menu != null && _menu.IsVisible)
        {
            double s = Ui.Scale;
            var bounds = new Rect(_menu.Left * s, _menu.Top * s,
                _menu.ActualWidth * s, _menu.ActualHeight * s);
            if (!bounds.Contains((double)x, (double)y)) CloseMenu();
            return;
        }
        int cx = ClickX, cy = ClickY, cr = ClickRadius;
        RegionConfig.Load(ref cx, ref cy, ref cr);
        if (Math.Abs(x - cx) <= cr && Math.Abs(y - cy) <= cr)
            ToggleMusic(x, y);
    }

    private void ToggleMusic(int px, int py)
    {
        bool? nowMuted = AudioController.Toggle(true); // 异步渐变: 立即返回+立即更新UI, 钩子回调不阻塞
        if (nowMuted == null)
        {
            Toast.Show("未检测到壁纸音乐", Toast.Kind.Warn, -1, -1);
        }
        else
        {
            UpdateTrayIcon(nowMuted.Value);
            string message = nowMuted.Value ? "音乐已关闭" : "音乐已开启";
            Toast.Show(message, nowMuted.Value ? Toast.Kind.Off : Toast.Kind.On, -1, -1);
        }
    }

    private void UpdateTrayIcon(bool muted)
    {
        if (_trayShowsOff == muted) return;
        _trayShowsOff = muted;
        _tray.Icon = muted ? _iconOff : _iconOn;
    }

    private void ShowMenu()
    {
        CloseMenu();
        bool? muted = AudioController.GetState();
        var menu = new PopupMenu(muted);
        menu.OnToggle = delegate { CloseMenu(); ToggleMusic(-1, -1); };
        menu.OnExit = delegate
        {
            CloseMenu();
            Application.Current.Shutdown(); // Run 返回后 Cleanup 会恢复音乐
        };
        menu.Closed += delegate { if (ReferenceEquals(_menu, menu)) _menu = null; };
        _menu = menu;

        menu.Show();
        var wa = WinForms.Screen.PrimaryScreen.WorkingArea;
        double s = Ui.Scale;

        // 优先: 定位在托盘图标上方, 左边界与图标正中心平齐
        SysDraw.Rectangle iconRect;
        if (TryGetTrayIconRect(out iconRect))
        {
            // 菜单左边界与图标矩形中心平齐, 底边贴屏幕最底端
            int iconCenterX = (iconRect.Left + iconRect.Right) / 2;
            menu.Left = iconCenterX / s;
            menu.Top = wa.Bottom / s - menu.ActualHeight;
            if (menu.Left < wa.Left / s) menu.Left = wa.Left / s + 2;
            if (menu.Left + menu.ActualWidth > wa.Right / s) menu.Left = wa.Right / s - menu.ActualWidth - 2;
        }
        else // 回退: 右下角
        {
            menu.Left = wa.Right / s - menu.ActualWidth - 8;
            menu.Top = wa.Bottom / s - menu.ActualHeight - 6;
        }
    }

    private IntPtr GetTrayIconHwnd()
    {
        try
        {
            var field = typeof(WinForms.NotifyIcon).GetField("window",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null) return IntPtr.Zero;
            var w = field.GetValue(_tray) as WinForms.NativeWindow;
            if (w == null) return IntPtr.Zero;
            return w.Handle;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>用 Shell_NotifyIconGetRect 获取托盘图标真实屏幕矩形(物理坐标)。
    /// 注意: WinForms 的 uID 用 1 而非 0(实测 uID=0 返回 E_FAIL)。</summary>
    private bool TryGetTrayIconRect(out SysDraw.Rectangle rect)
    {
        rect = SysDraw.Rectangle.Empty;
        try
        {
            IntPtr hwnd = GetTrayIconHwnd();
            if (hwnd == IntPtr.Zero) return false;
            var id = new NotifyIconIdentifier();
            id.cbSize = Marshal.SizeOf(typeof(NotifyIconIdentifier));
            id.hWnd = hwnd;
            id.uID = 1;
            id.guidItem = Guid.Empty;
            NativeRect r;
            if (Shell_NotifyIconGetRect(ref id, out r) != 0) return false;
            rect = new SysDraw.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return rect.Width > 0 && rect.Height > 0;
        }
        catch { return false; }
    }

    private void CloseMenu()
    {
        if (_menu != null) { _menu.Close(); _menu = null; }
    }

    private static SysDraw.Icon LoadIcon(string resourceName)
    {
        try
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null) return null;
                return new SysDraw.Icon(stream);
            }
        }
        catch { return null; }
    }
}

// ---------------------------------------------------------------------------
// 深色圆角右键菜单
// ---------------------------------------------------------------------------
internal class PopupMenu : Window
{
    public Action OnToggle;
    public Action OnExit;

    public PopupMenu(bool? muted)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 148;
        SizeToContent = SizeToContent.Height;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 11;
        SnapsToDevicePixels = true;

        var outer = new Border
        {
            Background = Brush(242, 22, 23, 26),
            CornerRadius = new CornerRadius(9),
            BorderBrush = Brush(44, 255, 255, 255),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(3),
            Margin = new Thickness(15), // 为阴影留出空间
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 0, Opacity = 0.45, Color = Colors.Black },
        };

        bool enabled = muted != null;
        string toggleLabel = enabled ? (muted.Value ? "开启音乐" : "关闭音乐") : "未检测到壁纸音乐";

        var stack = new StackPanel();
        stack.Children.Add(MakeItem("♪", toggleLabel, enabled,
            delegate { if (OnToggle != null) OnToggle(); }));
        stack.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(8, 2, 8, 2),
            Background = Brush(40, 255, 255, 255),
        });
        stack.Children.Add(MakeItem("✕", "退出", true,
            delegate { if (OnExit != null) OnExit(); }));

        outer.Child = stack;
        Content = outer;

        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private static Border MakeItem(string glyph, string label, bool enabled, Action onClick)
    {
        var hover = Brush(235, 66, 68, 76); // 灰色高亮
        var row = new Border
        {
            Height = 33, // 紧凑选项
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var g = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 11,
            Foreground = Brush(220, 255, 255, 255),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var t = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Brush(236, 236, 238, 240),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 7, 0),
        };
        Grid.SetColumn(t, 1);
        grid.Children.Add(g);
        grid.Children.Add(t);
        row.Child = grid;

        if (!enabled) { row.Opacity = 0.55; return row; }
        row.MouseEnter += delegate { row.Background = hover; };
        row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += delegate { if (onClick != null) onClick(); };
        return row;
    }

    internal static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }
}

// ---------------------------------------------------------------------------
// 深色圆角操作提示(短暂显示)
// ---------------------------------------------------------------------------
internal static class Toast
{
    internal enum Kind { On, Off, Warn }

    private static Window _current;

    public static void Show(string text, Kind kind, int px, int py)
    {
        if (_current != null) { _current.Close(); _current = null; }
        var win = new ToastWindow(text, kind);
        _current = win;
        win.Closed += delegate { if (ReferenceEquals(_current, win)) _current = null; };
        win.Show();
        win.PositionAt(px, py);
    }

    private class ToastWindow : Window
    {
        private static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
        {
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }

        public ToastWindow(string text, Kind kind)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            SnapsToDevicePixels = true;

            var border = new Border
            {
                Background = Brush(232, 22, 23, 26),
                CornerRadius = new CornerRadius(10),
                BorderBrush = Brush(36, 255, 255, 255),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 10, 18, 10),
                Margin = new Thickness(16),
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 0, Opacity = 0.4, Color = Colors.Black },
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            SolidColorBrush dotColor;
            if (kind == Kind.On) dotColor = Brush(255, 84, 204, 122);      // 绿: 音乐开启
            else if (kind == Kind.Off) dotColor = Brush(255, 225, 74, 74); // 红: 音乐关闭
            else dotColor = Brush(255, 235, 190, 60);                       // 黄: 未检测到
            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = dotColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = Brush(236, 236, 238, 240),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(tb, 1);
            grid.Children.Add(dot);
            grid.Children.Add(tb);
            border.Child = grid;
            Content = border;

            SourceInitialized += delegate { DisableActivation(this); };
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Opacity = 0;
            var fadeIn = new DoubleAnimation(0, 0.95, TimeSpan.FromMilliseconds(350));
            fadeIn.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, fadeIn);

            var tt = new TranslateTransform();
            RenderTransform = tt;
            var slide = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(350));
            slide.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.YProperty, slide);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += delegate
            {
                timer.Stop();
                var fadeOut = new DoubleAnimation(0.95, 0, TimeSpan.FromMilliseconds(550));
                fadeOut.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn };
                fadeOut.Completed += delegate { Close(); };
                BeginAnimation(OpacityProperty, fadeOut);
            };
            timer.Start();
        }

        public void PositionAt(int px, int py)
        {
            var wa = WinForms.Screen.PrimaryScreen.WorkingArea;
            double s = Ui.Scale;
            // 始终固定在右下角, 底缘距屏幕底部 14 物理px(=7 DIP @200%)(物理坐标 -> WPF 逻辑坐标)
            Left = (wa.Right - 14) / s - ActualWidth;
            Top = (wa.Bottom - 7) / s - ActualHeight;
        }

        private void DisableActivation(Window w)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            int ex = GetWindowLong(hwnd, GwlExstyle);
            SetWindowLong(hwnd, GwlExstyle,
                ex | WsExNoactivate | WsExToolWindow | WsExTopmost);
        }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        private const int GwlExstyle = -20;
        private const int WsExNoactivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExTopmost = 0x00000008;
    }
}
