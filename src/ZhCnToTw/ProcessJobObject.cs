using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZhCnToTw;

/// <summary>
/// Windows Job Object 包一層：把子行程綁進這個 job，job handle 所在的
/// process（這支殼自己）結束時，Windows 核心會自動連坐砍掉所有綁在這個
/// job 裡的子行程——不管殼是正常關閉還是被系統/使用者直接砍掉（工作
/// 管理員、崩潰），都不會留下孤兒 ocr-service process 佔用 port/記憶體。
///
/// 這是 Mac 版孤兒看門狗（zh-cn-to-tw-ocr-service 的 app.py 裡
/// _orphan_watchdog，每 5 秒輪詢 os.getppid() 有沒有變）在 Windows 上的
/// 對應機制，但更可靠：POSIX 系統 parent 死掉後子行程會被重新掛到別的
/// parent（通常是 init/launchd），getppid() 讀到的值真的會變，輪詢才抓
/// 得到；Windows 不會重新掛接子行程的 parent pid——那個值是建立當下就
/// 固定住的，parent 死了也不會變，除非剛好有新 process 巧合搶到同一個
/// pid，Python 那套輪詢邏輯在 Windows 上基本上失效。Job Object 是作業
/// 系統核心層級的保證，不受這個問題影響，也沒有輪詢延遲的空窗。
/// </summary>
internal static class ProcessJobObject
{
    private static readonly IntPtr JobHandle = CreateJob();

    /// <summary>
    /// 把子行程綁進這個 job。建立 job 本身失敗（理論上不該發生，但
    /// P/Invoke 到系統 API 一律假設可能失敗）就靜默放棄，不影響正常的
    /// 啟動/停止流程——這只是一層額外保險，不是核心功能。
    /// </summary>
    public static void Assign(Process process)
    {
        if (JobHandle == IntPtr.Zero) return;
        AssignProcessToJobObject(JobHandle, process.Handle);
    }

    private static IntPtr CreateJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return IntPtr.Zero;

        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var infoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(extendedInfo, infoPtr, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, infoPtr, (uint)length))
            {
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }
        return handle;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
