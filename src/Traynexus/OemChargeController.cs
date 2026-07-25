using System;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Traynexus
{
    /// <summary>
    /// OEM 充电阈值控制器：通过厂商驱动设备的 IOCTL 接口设置充电上限。
    ///
    /// 实现路线（基于开源项目 g-helper / LenovoLegionToolkit 的协议调研，代码自行编写）：
    /// - ASUS：\\.\ATKACPI 设备 + IOCTL 0x0022240C，ACPI DEVS(0x53564544)/DSTS(0x53545344) 方法
    ///   充电阈值 DEVID = 0x00120057，支持 40-100% 任意值（部分 6080 机型固件锁三档）
    /// - Lenovo：\\.\EnergyDrv 设备 + IOCTL 0x831020F8
    ///   仅 3 模式：保养{0x08,0x03} / 正常{0x05,0x08} / 快充{0x05,0x07}，不支持任意阈值
    /// - Dell：依赖 Dell Command Configure CLI (cctk.exe)，第一版未实现
    /// - HP：无公开 API，第一版未实现
    ///
    /// 能力检测靠「试调用」：打开设备句柄 + 发一次查询 IOCTL，不抛异常即支持。
    /// 协议常数（DEVID/IOCTL 码/设备路径）属硬件接口事实，不受版权约束。
    /// </summary>
    public static class OemChargeController
    {
        // ==== ASUS ACPI 协议常数 ====
        private const string ASUS_DEVICE = @"\\.\ATKACPI";
        private const uint ASUS_IOCTL = 0x0022240C;
        private const uint ASUS_DEVS = 0x53564544;  // ASCII "DEVS" (Device Set)
        private const uint ASUS_DSTS = 0x53545344;  // ASCII "DSTS" (Device Status)
        private const uint ASUS_DEVID_BATTERY_LIMIT = 0x00120057;

        // ==== Lenovo EnergyDrv 协议常数 ====
        private const string LENOVO_DEVICE = @"\\.\EnergyDrv";
        private const uint LENOVO_IOCTL_CHARGE_MODE = 0x831020F8;
        // 查询当前模式时发送的 inBuffer 值
        private const uint LENOVO_QUERY_CMD = 0xFF;
        // 设置模式时依次发送的命令序列
        private static readonly uint[] LENOVO_MODE_CONSERVATION = { 0x08, 0x03 };  // 保养（60% 上限）
        private static readonly uint[] LENOVO_MODE_NORMAL       = { 0x05, 0x08 };  // 正常（满充）
        private static readonly uint[] LENOVO_MODE_RAPID        = { 0x05, 0x07 };  // 快充
        // 回值位掩码：bit 0x20=保养，bit 0x04=快充，其余=正常
        private const uint LENOVO_BIT_CONSERVATION = 0x20;
        private const uint LENOVO_BIT_RAPID        = 0x04;

        // ==== 能力缓存（避免每次设置都重新探测设备句柄）====
        private static ChargeCapability _cachedCap;
        private static DateTime _cacheTime = DateTime.MinValue;
        private static readonly object _cacheLock = new object();
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        // ==== 状态缓存（避免 TickRefresh 每秒 IOCTL）====
        private static ChargeStatus _cachedStatus;
        private static DateTime _statusTime = DateTime.MinValue;
        private static readonly TimeSpan StatusTtl = TimeSpan.FromSeconds(10);

        // ==== IOCTL 互斥锁（防止滑块拖动/并发操作同时打开设备句柄）====
        private static readonly object _ioctlLock = new object();

        /// <summary>
        /// 检测当前设备的充电阈值控制能力。结果缓存 5 分钟。
        /// 线程安全。
        /// </summary>
        public static ChargeCapability GetCapability()
        {
            lock (_cacheLock)
            {
                if (_cachedCap != null && DateTime.Now - _cacheTime < CacheTtl)
                    return _cachedCap;
            }

            var cap = new ChargeCapability();
            try
            {
                string manufacturer = ReadManufacturer();
                string model = ReadModel();
                cap.Manufacturer = manufacturer;
                cap.Model = model;
                string m = (manufacturer ?? "").ToUpperInvariant();

                if (m.Contains("ASUS") || m.Contains("ASUSTEK"))
                {
                    cap.Oem = OemVendor.Asus;
                    ProbeAsus(cap);
                }
                else if (m.Contains("LENOVO"))
                {
                    cap.Oem = OemVendor.Lenovo;
                    ProbeLenovo(cap);
                }
                else if (m.Contains("DELL"))
                {
                    cap.Oem = OemVendor.Dell;
                    cap.Supported = false;
                    cap.DriverName = "Dell Command | Power Manager";
                    cap.Hint = "Dell 充电控制需要 Dell Command Configure CLI，本版本暂未集成。";
                }
                else if (m.Contains("HP") || m.Contains("HEWLETT"))
                {
                    cap.Oem = OemVendor.HP;
                    cap.Supported = false;
                    cap.DriverName = "HP Power Manager";
                    cap.Hint = "HP 充电控制无公开 API，本版本暂未支持。";
                }
                else
                {
                    cap.Oem = OemVendor.Unknown;
                    cap.Supported = false;
                    cap.Hint = "未识别的设备厂商，不支持充电阈值控制。";
                }
            }
            catch (Exception ex)
            {
                Settings.Log("OemChargeController.GetCapability 失败: " + ex.Message);
                cap.Oem = OemVendor.Unknown;
                cap.Supported = false;
                cap.Hint = "检测失败: " + ex.Message;
            }

            lock (_cacheLock)
            {
                _cachedCap = cap;
                _cacheTime = DateTime.Now;
            }
            return cap;
        }

        /// <summary>清除能力缓存，强制下次 GetCapability 重新探测。诊断面板「重新检测」时调用。</summary>
        public static void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedCap = null;
                _cacheTime = DateTime.MinValue;
                _cachedStatus = null;
                _statusTime = DateTime.MinValue;
            }
        }

        /// <summary>
        /// 设置充电上限百分比。返回是否成功。
        /// Lenovo 因固件限制会把任意百分比映射到 3 档模式。
        /// 加互斥锁防止滑块拖动等并发触发同时打开设备句柄。
        /// </summary>
        public static bool SetChargeLimit(int percent)
        {
            if (percent < 40 || percent > 100) return false;
            var cap = GetCapability();
            if (!cap.Supported) return false;

            // ASUS：单次 IOCTL 无 Sleep，持锁时间极短，整体在锁内完成
            if (cap.Oem == OemVendor.Asus)
            {
                lock (_ioctlLock)
                {
                    try
                    {
                        bool ok = SetAsusLimit(percent);
                        if (ok) ClearStatusCache();
                        return ok;
                    }
                    catch (Exception ex)
                    {
                        Settings.Log("OemChargeController.SetChargeLimit(ASUS) 失败: " + ex.Message);
                        return false;
                    }
                }
            }

            // Lenovo：双命令连发（需锁防交叉）+ 回读校验（10×50ms Sleep，移出锁避免阻塞 GetStatus）
            if (cap.Oem == OemVendor.Lenovo)
            {
                uint[] cmds = percent >= 80 ? LENOVO_MODE_NORMAL : LENOVO_MODE_CONSERVATION;
                int expectedMode = (cmds == LENOVO_MODE_CONSERVATION) ? 0 : 2;

                // 阶段1：锁内发双命令（含 2×50ms Sleep，约 100ms）
                bool sent;
                lock (_ioctlLock)
                {
                    try { sent = SendLenovoCommands(cmds); }
                    catch (Exception ex)
                    {
                        Settings.Log("OemChargeController.SetChargeLimit(Lenovo 发命令) 失败: " + ex.Message);
                        return false;
                    }
                }
                if (!sent) return false;

                // 阶段2：锁外回读校验（最多 10×50ms=500ms），期间 GetStatus 可正常抢锁读状态
                bool ok = VerifyLenovoMode(expectedMode);
                if (!ok) Settings.Log("Lenovo SetChargeLimit: 回读不匹配，期望=" + expectedMode);
                if (ok) ClearStatusCache();
                return ok;
            }

            return false;
        }

        /// <summary>清状态缓存，让下次 GetStatus 立即读到新值。</summary>
        private static void ClearStatusCache()
        {
            lock (_cacheLock)
            {
                _cachedStatus = null;
                _statusTime = DateTime.MinValue;
            }
        }

        /// <summary>Lenovo 发双命令（持锁调用）。每条后 sleep 50ms。返回是否两条都成功发出。</summary>
        private static bool SendLenovoCommands(uint[] cmds)
        {
            IntPtr h = OpenDevice(LENOVO_DEVICE);
            if (h == NativeMethods.INVALID_HANDLE_VALUE) return false;
            try
            {
                foreach (uint cmd in cmds)
                {
                    CallLenovoIoctl(h, cmd);
                    System.Threading.Thread.Sleep(50);
                }
                return true;
            }
            finally { NativeMethods.CloseHandle(h); }
        }

        /// <summary>Lenovo 回读校验（锁外调用）。10 次×50ms，匹配 expectedMode 即成功。</summary>
        private static bool VerifyLenovoMode(int expectedMode)
        {
            for (int i = 0; i < 10; i++)
            {
                if (QueryLenovoMode() == expectedMode) return true;
                System.Threading.Thread.Sleep(50);
            }
            return false;
        }

        /// <summary>
        /// 查询当前充电模式/阈值，供诊断面板和充电页验证。
        /// 结果缓存 10 秒，避免 TickRefresh 每秒打开设备句柄。
        /// 返回 null 表示无法读取。
        /// </summary>
        public static ChargeStatus GetStatus()
        {
            lock (_cacheLock)
            {
                if (_cachedStatus != null && DateTime.Now - _statusTime < StatusTtl)
                    return _cachedStatus;
            }

            var cap = GetCapability();
            if (!cap.Supported) return null;

            ChargeStatus result = null;
            lock (_ioctlLock)
            {
                try
                {
                    if (cap.Oem == OemVendor.Asus)
                    {
                        int? v = QueryAsusLimit();
                        if (v.HasValue) result = new ChargeStatus { LimitPercent = v.Value, ModeName = v.Value + "%" };
                    }
                    else if (cap.Oem == OemVendor.Lenovo)
                    {
                        int mode = QueryLenovoMode();
                        string name;
                        int limit;
                        switch (mode)
                        {
                            case 0: name = "保养"; limit = 60; break;
                            case 1: name = "快充"; limit = 100; break;
                            default: name = "正常"; limit = 100; break;
                        }
                        result = new ChargeStatus { LimitPercent = limit, ModeName = name };
                    }
                }
                catch (Exception ex)
                {
                    Settings.Log("OemChargeController.GetStatus 失败: " + ex.Message);
                }
            }

            // 缓存结果（含 null/失败），避免每秒重试 IOCTL
            lock (_cacheLock)
            {
                _cachedStatus = result;
                _statusTime = DateTime.Now;
            }
            return result;
        }

        // ============================================================
        // 厂商探测
        // ============================================================

        private static void ProbeAsus(ChargeCapability cap)
        {
            cap.DevicePath = ASUS_DEVICE;
            cap.DriverName = "ASUS System Control Interface";
            cap.MinThreshold = 40;
            cap.MaxThreshold = 100;
            cap.ModeType = ChargeModeType.Continuous;
            // 试打开设备句柄
            IntPtr h = OpenDevice(ASUS_DEVICE);
            if (h == NativeMethods.INVALID_HANDLE_VALUE)
            {
                cap.Supported = false;
                cap.Hint = "未检测到 ASUS ATKACPI 驱动。请安装 ASUS System Control Interface 驱动（通常随 MyASUS 提供）。";
                return;
            }
            try
            {
                // 发一次 DSTS 查询验证驱动响应正常（不验证返回值，只验证不抛异常）
                CallAsusMethod(h, ASUS_DSTS, ASUS_DEVID_BATTERY_LIMIT, 0);
                cap.Supported = true;
                cap.Hint = "ASUS 充电阈值控制就绪，支持 40-100% 任意值。";
            }
            catch (Exception ex)
            {
                cap.Supported = false;
                cap.Hint = "ASUS 驱动已安装但不支持充电阈值控制: " + ex.Message;
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }

        private static void ProbeLenovo(ChargeCapability cap)
        {
            cap.DevicePath = LENOVO_DEVICE;
            cap.DriverName = "Lenovo Energy Management Driver";
            cap.SupportedThresholds = new int[] { 60, 80, 100 };
            cap.ModeType = ChargeModeType.ThreeMode;
            cap.MinThreshold = 60;
            cap.MaxThreshold = 100;

            // 检测 \\.\EnergyDrv 设备（由 AcpiVpc.sys 驱动创建，随 EM 驱动安装）
            IntPtr h = OpenDevice(LENOVO_DEVICE);
            if (h == NativeMethods.INVALID_HANDLE_VALUE)
            {
                cap.Supported = false;
                cap.Hint = "未检测到 Lenovo EnergyDrv 设备。请安装 Lenovo Energy Management 驱动启用充电控制。";
                return;
            }
            try
            {
                // 查询当前模式验证驱动响应
                int mode = QueryLenovoMode(h);
                cap.Supported = true;
                cap.Hint = "Lenovo 充电控制就绪（EnergyDrv）。支持保养(60%)/正常(100%)/快充(100%) 三档模式。";
            }
            catch (Exception ex)
            {
                cap.Supported = false;
                cap.Hint = "EnergyDrv 设备存在但不支持充电控制: " + ex.Message;
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }

        // ============================================================
        // ASUS IOCTL 实现
        // ============================================================

        private static bool SetAsusLimit(int percent)
        {
            IntPtr h = OpenDevice(ASUS_DEVICE);
            if (h == NativeMethods.INVALID_HANDLE_VALUE) return false;
            try
            {
                // DEVS 方法：args = [0..3] DEVID + [4..7] 值
                CallAsusMethod(h, ASUS_DEVS, ASUS_DEVID_BATTERY_LIMIT, (uint)percent);
                return true;
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }

        private static int? QueryAsusLimit()
        {
            IntPtr h = OpenDevice(ASUS_DEVICE);
            if (h == NativeMethods.INVALID_HANDLE_VALUE) return null;
            try
            {
                byte[] result = CallAsusMethod(h, ASUS_DSTS, ASUS_DEVID_BATTERY_LIMIT, 0);
                if (result == null || result.Length < 4) return null;
                // DSTS 返回值：低字节为状态码，部分机型在更高位编码阈值
                // 这里仅返回非 0 状态码表示可读，精确阈值需机型适配，第一版返回 null
                return null;
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }

        /// <summary>
        /// 调用 ASUS ACPI 方法（DEVS 设置 / DSTS 查询）。
        /// 输入缓冲区格式：[0..3] MethodID + [4..7] args 长度 + [8..] args 内容
        /// args 内容：[0..3] DeviceID + [4..7] Status
        /// </summary>
        private static byte[] CallAsusMethod(IntPtr h, uint methodId, uint deviceId, uint status)
        {
            // 构造 ACPI 调用缓冲区
            byte[] args = new byte[8];
            WriteUInt32LE(args, 0, deviceId);
            WriteUInt32LE(args, 4, status);

            byte[] inBuf = new byte[8 + args.Length];
            WriteUInt32LE(inBuf, 0, methodId);
            WriteUInt32LE(inBuf, 4, (uint)args.Length);
            Array.Copy(args, 0, inBuf, 8, args.Length);

            byte[] outBuf = new byte[16];
            uint returned;
            if (!NativeMethods.DeviceIoControl(h, ASUS_IOCTL, inBuf, (uint)inBuf.Length,
                outBuf, (uint)outBuf.Length, out returned, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException("DeviceIoControl 失败 Win32Error=" + err);
            }
            return outBuf;
        }

        // ============================================================
        // Lenovo IOCTL 实现（通过 EnergyDrv 设备 + AcpiVpc.sys 驱动）
        // ============================================================
        // 只需安装 Lenovo Energy Management 驱动（5MB），不需要 Vantage（557MB）。
        // 驱动创建 \\.\EnergyDrv 符号链接，IOCTL 0x831020F8 控制充电模式。
        // 保养模式：{0x08, 0x03}，正常：{0x05, 0x08}，快充：{0x05, 0x07}
        // 查询：input=0xFF，返回值 bit5(0x20)=保养，bit2(0x04)=快充

        /// <summary>返回 0=保养 / 1=快充 / 2=正常，-1=读取失败</summary>
        private static int QueryLenovoMode()
        {
            IntPtr h = OpenDevice(LENOVO_DEVICE);
            if (h == NativeMethods.INVALID_HANDLE_VALUE) return -1;
            try { return QueryLenovoMode(h); }
            finally { NativeMethods.CloseHandle(h); }
        }

        private static int QueryLenovoMode(IntPtr h)
        {
            byte[] inBuf = BitConverter.GetBytes(LENOVO_QUERY_CMD);
            byte[] outBuf = new byte[4];
            uint returned;
            if (!NativeMethods.DeviceIoControl(h, LENOVO_IOCTL_CHARGE_MODE, inBuf, (uint)inBuf.Length,
                outBuf, (uint)outBuf.Length, out returned, IntPtr.Zero))
                return -1;
            uint state = BitConverter.ToUInt32(outBuf, 0);
            if ((state & LENOVO_BIT_CONSERVATION) != 0) return 0;
            if ((state & LENOVO_BIT_RAPID) != 0) return 1;
            return 2;
        }

        private static void CallLenovoIoctl(IntPtr h, uint cmd)
        {
            byte[] inBuf = BitConverter.GetBytes(cmd);
            byte[] outBuf = new byte[4];
            uint returned;
            if (!NativeMethods.DeviceIoControl(h, LENOVO_IOCTL_CHARGE_MODE, inBuf, (uint)inBuf.Length,
                outBuf, (uint)outBuf.Length, out returned, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException("Lenovo DeviceIoControl 失败 Win32Error=" + err);
            }
        }

        // ============================================================
        // 工具方法
        // ============================================================

        private static IntPtr OpenDevice(string path)
        {
            return NativeMethods.CreateFileW(
                path,
                NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);
        }

        private static string ReadManufacturer()
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_ComputerSystem"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    try { return Convert.ToString(mo["Manufacturer"]); }
                    finally { try { mo.Dispose(); } catch { } }
                }
            }
            return "";
        }

        private static string ReadModel()
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    try { return Convert.ToString(mo["Model"]); }
                    finally { try { mo.Dispose(); } catch { } }
                }
            }
            return "";
        }

        private static void WriteUInt32LE(byte[] buf, int offset, uint value)
        {
            buf[offset + 0] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }

    /// <summary>OEM 厂商标识。</summary>
    public enum OemVendor
    {
        Unknown = 0,
        Lenovo = 1,
        Dell = 2,
        HP = 3,
        Asus = 4
    }

    /// <summary>充电控制模式类型。</summary>
    public enum ChargeModeType
    {
        /// <summary>连续阈值（如 ASUS 40-100% 任意值）</summary>
        Continuous,
        /// <summary>固定档位（如 Lenovo 保养/正常/快充 三档）</summary>
        ThreeMode
    }

    /// <summary>充电阈值控制能力检测结果。</summary>
    public class ChargeCapability
    {
        public OemVendor Oem;
        public bool Supported;
        public string Manufacturer = "";
        public string Model = "";
        public string DevicePath = "";
        public string DriverName = "";
        public string Hint = "";
        public int MinThreshold = 50;
        public int MaxThreshold = 100;
        /// <summary>固定档位列表（Lenovo={60,80,100}）；null 表示连续阈值</summary>
        public int[] SupportedThresholds = null;
        public ChargeModeType ModeType = ChargeModeType.Continuous;
    }

    /// <summary>充电状态回读结果。</summary>
    public class ChargeStatus
    {
        public int LimitPercent;
        public string ModeName;
    }
}
