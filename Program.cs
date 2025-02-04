using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Management;
using Microsoft.Win32;

public static class NTapi
{
    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, ref int ReturnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryObject(IntPtr Handle, int ObjectInformationClass, IntPtr ObjectInformation, int ObjectInformationLength, ref int ReturnLength);

    [DllImport("ntdll.dll")]
    public static extern int NtClose(IntPtr Handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}

class Program : Form
{
    private struct UNI
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NAME
    {
        public UNI Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HANDLE
    {
        public int ProcessId;
        public byte ObjectTypeNumber;
        public byte Flags;
        public ushort Handle;
        public IntPtr Object;
        public uint GrantedAccess;
    }

    private NotifyIcon trayIcon;
    private Queue<Process> ProcQueue = new Queue<Process>();
    private HashSet<IntPtr> Handles = new HashSet<IntPtr>();
    private ToolStripMenuItem menuStatus;
    private ManagementEventWatcher Watcher;
    private readonly string ProcessName = "RobloxPlayerBeta";
    private readonly string[] OrderedHandles = new string[] { "singletonmutex", "singletonevent" };
    public string errorState = "";
    public (int instances, int handles, int errors) counts = (0, 0, 0);

    static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            Exception e = (Exception)args.ExceptionObject;
            _ = MessageBox.Show("Unhandled exception: " + e.Message + "\n" + e.StackTrace);
        };

        if (!CheckDotNet()) return;

        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Program());
    }

    public Program()
    {
        this.trayIcon = new NotifyIcon
        {
            Icon = new Icon(this.GetType().Assembly.GetManifestResourceStream("app.Resources.icon.ico")),
            ContextMenuStrip = new ContextMenuStrip(),
            Visible = true
        };
        this.WindowState = FormWindowState.Minimized;
        this.ShowInTaskbar = false;
        this.menuStatus = new ToolStripMenuItem { Enabled = false };
        _ = this.trayIcon.ContextMenuStrip.Items.Add(this.menuStatus);
        _ = this.trayIcon.ContextMenuStrip.Items.Add("Exit", null, (s, e) =>
        {
            this.trayIcon.Visible = false;
            Application.Exit();
        });

        foreach (Process p in Process.GetProcessesByName(this.ProcessName))
        {
            this.ProcQueue.Enqueue(p);
        }

        this.Watcher = new ManagementEventWatcher(
            new WqlEventQuery(
                "__InstanceCreationEvent",
                new TimeSpan(0, 0, 1),
                "TargetInstance isa 'Win32_Process' and TargetInstance.Name = '" + this.ProcessName + ".exe'"
            )
        );

        this.Watcher.EventArrived += async (s, e) =>
        {
            int processId = Convert.ToInt32(((ManagementBaseObject)e.NewEvent["TargetInstance"])["ProcessId"]);
            await Task.Run(() => this.ProcQueue.Enqueue(Process.GetProcessById(processId)));
        };

        this.Watcher.Start();
        _ = Task.Run(() => this.MainLoop());
        _ = Task.Run(() => this.StatusLoop());
    }

    static bool CheckDotNet()
    {
        const string registryPath = @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost";
        const string versionKey = "Version";

        try
        {
            using RegistryKey ndpKey = Registry.LocalMachine.OpenSubKey(registryPath);
            if (ndpKey == null) return ShowErrorAndPrompt();
            object versionObj = ndpKey.GetValue(versionKey);
            if (versionObj == null) return ShowErrorAndPrompt();
            Version installedVersion = new Version(versionObj.ToString());
            if (installedVersion.Major < 8) return ShowErrorAndPrompt();
            return true;
        }
        catch
        {
            return ShowErrorAndPrompt();
        }
    }

    static bool ShowErrorAndPrompt()
    {
        _ = MessageBox.Show("The required .NET runtime is not installed. Please install the latest .NET runtime from the official website.",
                        "Runtime Missing",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = "https://dotnet.microsoft.com/download",
            UseShellExecute = true
        });

        return false;
    }

    protected override void OnLoad(EventArgs e)
    {
        this.Visible = false;
        this.ShowInTaskbar = false;
        base.OnLoad(e);
    }

    private async Task MainLoop()
    {
        while (true)
        {
            if (this.ProcQueue.Count == 0)
            {
                await Task.Delay(1000);
                continue;
            }
            else
            {
                await Task.Delay(50);
                await ProcessQueued();
            }
        }
    }

    private async Task StatusLoop()
    {
        while (true)
        {
            this.menuStatus.Text = "Instances: " + this.counts.instances.ToString();
            this.menuStatus.Text += " | Handles: " + this.counts.handles.ToString();
            this.menuStatus.Text += " | Errors: " + this.counts.errors.ToString();
            await Task.Delay(1000);
        }
    }

    private async Task ProcessQueued()
    {
        Process currentProcess = this.ProcQueue.Peek();
        try
        {
            if (await this.CloseHandles(await this.UpdateHandleList(currentProcess)))
            {
                _ = this.ProcQueue.Dequeue();
                this.counts.instances++;
            }
            else
            {
                await this.BumpQueue();
            }
        }
        catch (Exception ex)
        {
            this.errorState = "Error processing: " + (currentProcess != null ? currentProcess.Id.ToString() : "Unknown") + " - " + ex.Message;
            await this.BumpQueue();
            this.counts.errors++;
        }
    }

    private async Task BumpQueue()
    {
        this.ProcQueue.Enqueue(this.ProcQueue.Dequeue());
    }

    private async Task<bool> CloseHandles(Process process)
    {
        const uint MAXIMUM_ALLOWED = 0x02000000;
        const uint PROCESS_DUP_HANDLE = 0x00000040;
        IntPtr processHandle = NTapi.OpenProcess(PROCESS_DUP_HANDLE, false, process.Id);
        if (processHandle == IntPtr.Zero) return false;

        Queue<string> tdl = new(this.OrderedHandles);

        while (tdl.Count > 0)
        {
            string target = tdl.Peek();
            bool found = false;

            foreach (var h in this.Handles)
            {
                IntPtr alloc = Marshal.AllocHGlobal(0x10000);
                if (!NTapi.DuplicateHandle(processHandle, h, NTapi.GetCurrentProcess(), out nint dup, MAXIMUM_ALLOWED, false, 0))
                {
                    Marshal.FreeHGlobal(alloc);
                    continue;
                }

                int returnLength = 0;
                _ = NTapi.NtQueryObject(dup, 1, alloc, 0x10000, ref returnLength);
                NAME nameInfo = (NAME)Marshal.PtrToStructure(alloc, typeof(NAME));
                string name = nameInfo.Name.Buffer != IntPtr.Zero ? Marshal.PtrToStringUni(nameInfo.Name.Buffer, nameInfo.Name.Length / 2) : null;

                if (name != null && name.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = true;
                    _ = await ForceClose(h, processHandle);
                    _ = tdl.Dequeue();
                }

                Marshal.FreeHGlobal(alloc);
                _ = NTapi.NtClose(dup);

                if (found) break;
            }

            if (!found) break;
        }

        _ = NTapi.CloseHandle(processHandle);

        return tdl.Count == 0;
    }

    private static async Task<bool> ForceClose(IntPtr handle, IntPtr processHandle)
    {
        return await Task.Run(() =>
        {
            bool result = NTapi.DuplicateHandle(processHandle, handle, NTapi.GetCurrentProcess(), out nint dupHandle, 0, false, 0x00000001);
            _ = NTapi.NtClose(dupHandle);
            return result;
        });
    }

    private async Task<Process> UpdateHandleList(Process process)
    {
        return await Task.Run(() =>
         {
             int dataSize = 0x10000, length = 0;
             IntPtr dataPtr = Marshal.AllocHGlobal(dataSize);

             try
             {
                 while (NTapi.NtQuerySystemInformation(16, dataPtr, dataSize, ref length) == unchecked((int)0xC0000004))
                 {
                     dataSize = length;
                     Marshal.FreeHGlobal(dataPtr);
                     dataPtr = Marshal.AllocHGlobal(length);
                 }

                 IntPtr itemPtr = dataPtr + IntPtr.Size;
                 int handleCount = Marshal.ReadInt32(dataPtr);
                 for (int i = 0; i < handleCount; i++, itemPtr += Marshal.SizeOf(typeof(HANDLE)))
                 {
                     HANDLE handleInfo = (HANDLE)Marshal.PtrToStructure(itemPtr, typeof(HANDLE));
                     if (handleInfo.ProcessId == process.Id) _ = this.Handles.Add(new IntPtr(handleInfo.Handle));
                 }
             }
             catch (Exception ex)
             {
                 throw new InvalidOperationException("Failed to retrieve handle list for process " + process.Id, ex);
             }
             finally
             {
                 Marshal.FreeHGlobal(dataPtr);
             }

             return process;
         });
    }
}


public class HandleManager
{
    public async Task<bool> CloseHandles(Process process, string[] targets)
    {
        foreach (var target in targets)
        {
            if (!await ProcessTargetHandle(process, target))
                return false;
        }
        return true;
    }

    private async Task<bool> ProcessTargetHandle(Process process, string target)
    {
        return await Task.FromResult(true);
    }
}
