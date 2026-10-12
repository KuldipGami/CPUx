using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CPUx.Core;

/// <summary>
/// Behavior that differs when CPUx runs from its MSIX (Microsoft Store) package: registry Run entries are
/// virtualized there, so "Start with Windows" uses the package's startup task instead. The WinRT calls go
/// through raw vtables to avoid pulling in the 25 MB Windows SDK projection for three methods.
/// </summary>
public static unsafe class PackagedApp
{
    public const string StartupTaskId = "CPUxStartup";

    private static readonly Guid IStartupTaskStatics = new("ee5b60bd-a148-41a7-b26e-e8b88a1e62f8");
    private static readonly Guid IAppInstanceStatics = new("9d11e77f-9ea6-47af-a6ec-46784c5ba254");
    private static readonly Guid IActivatedEventArgs = new("cf651713-cd08-4fd8-b697-a281b6544e2e");
    private static readonly Guid IAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref int length, char* name);

    [DllImport("combase.dll")]
    private static extern int RoInitialize(int type);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, void** factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    /// <summary>True when running with package identity (installed from the MSIX / Store).</summary>
    public static bool IsPackaged { get; } = DetectPackage();

    /// <summary>
    /// Whether "Restart as administrator" can work: always for the installer/portable builds; for the Store package
    /// only when its manifest declares allowElevation (a restricted capability the Store rarely approves).
    /// </summary>
    public static bool CanElevate { get; } = !IsPackaged || ManifestDeclares("allowElevation");

    private static bool ManifestDeclares(string capability)
    {
        try
        {
            var manifest = System.IO.Path.Combine(AppContext.BaseDirectory, "AppxManifest.xml");
            return System.IO.File.Exists(manifest) && System.IO.File.ReadAllText(manifest).Contains($"\"{capability}\"");
        }
        catch { return false; }
    }

    private static bool DetectPackage()
    {
        try
        {
            int len = 0;
            return GetCurrentPackageFullName(ref len, null) != 15700; // APPMODEL_ERROR_NO_PACKAGE
        }
        catch { return false; }
    }

    /// <summary>True when Windows started the app through its startup task (sign-in), so it should open minimized.</summary>
    public static bool LaunchedByStartupTask()
    {
        if (!IsPackaged) return false;
        void* statics = null, args = null;
        try
        {
            statics = Factory("Windows.ApplicationModel.AppInstance", IAppInstanceStatics);
            if (statics == null) return false;
            if (Call(statics, 7, &args) < 0 || args == null) return false; // GetActivatedEventArgs
            void* activated = null;
            if (QueryInterface(args, IActivatedEventArgs, &activated) < 0) return false;
            int kind = 0;
            ((delegate* unmanaged[Stdcall]<void*, int*, int>)VTable(activated)[6])(activated, &kind); // get_Kind
            Release(activated);
            return kind == 1020; // ActivationKind.StartupTask
        }
        catch (Exception ex)
        {
            Log.Error("Activation kind", ex);
            return false;
        }
        finally
        {
            Release(args);
            Release(statics);
        }
    }

    /// <summary>Whether the startup task is enabled; null if it can't be read.</summary>
    public static bool? StartupEnabled()
    {
        var state = WithStartupTask(handle =>
        {
            void* task = (void*)handle;
            int s = 0;
            ((delegate* unmanaged[Stdcall]<void*, int*, int>)VTable(task)[8])(task, &s); // get_State
            return s;
        });
        return state is null ? null : state is 2 or 4; // Enabled, EnabledByPolicy
    }

    /// <summary>Turns the startup task on or off. Returns the resulting state (false when the user disabled it in Settings).</summary>
    public static bool SetStartup(bool enable)
    {
        var state = WithStartupTask(handle =>
        {
            void* task = (void*)handle;
            if (!enable)
            {
                ((delegate* unmanaged[Stdcall]<void*, int>)VTable(task)[7])(task); // Disable
                return 0;
            }
            void* op = null;
            if (Call(task, 6, &op) < 0) return -1; // RequestEnableAsync
            long result = Await(op);
            Release(op);
            return (int)result;
        });
        return state is 2 or 4;
    }

    private static int? WithStartupTask(Func<IntPtr, int> action) => RunMta(() =>
    {
        if (!IsPackaged) return (int?)null;
        void* statics = Factory("Windows.ApplicationModel.StartupTask", IStartupTaskStatics);
        if (statics == null) return null;
        IntPtr id = IntPtr.Zero;
        void* op = null;
        try
        {
            WindowsCreateString(StartupTaskId, StartupTaskId.Length, out id);
            int hr = ((delegate* unmanaged[Stdcall]<void*, IntPtr, void**, int>)VTable(statics)[7])(statics, id, &op); // GetAsync(taskId)
            if (hr < 0 || op == null) return null;
            void* task = (void*)Await(op);
            if (task == null) return null;
            try { return action((IntPtr)task); }
            finally { Release(task); }
        }
        finally
        {
            Release(op);
            if (id != IntPtr.Zero) WindowsDeleteString(id);
            Release(statics);
        }
    });

    private static T RunMta<T>(Func<T> f)
    {
        // WinRT async objects are polled here; doing that on a worker thread keeps the UI thread free.
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                RoInitialize(1); // multithreaded
                result = f();
            }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        t.Start();
        t.Join(10000);
        if (error != null) Log.Error("Startup task", error);
        return result;
    }

    private static long Await(void* op)
    {
        void* info = null;
        if (QueryInterface(op, IAsyncInfo, &info) < 0) return 0;
        try
        {
            var sw = Stopwatch.StartNew();
            int status = 0;
            while (true)
            {
                ((delegate* unmanaged[Stdcall]<void*, int*, int>)VTable(info)[7])(info, &status); // get_Status
                if (status != 0 || sw.ElapsedMilliseconds > 8000) break; // 0 = Started
                Thread.Sleep(10);
            }
            if (status != 1) return 0; // 1 = Completed
            long result = 0;
            ((delegate* unmanaged[Stdcall]<void*, long*, int>)VTable(op)[8])(op, &result); // GetResults
            return result;
        }
        finally { Release(info); }
    }

    private static void* Factory(string className, Guid iid)
    {
        if (WindowsCreateString(className, className.Length, out var h) < 0) return null;
        try
        {
            void* f = null;
            return RoGetActivationFactory(h, iid, &f) >= 0 ? f : null;
        }
        finally { WindowsDeleteString(h); }
    }

    private static void** VTable(void* obj) => *(void***)obj;

    private static int Call(void* obj, int slot, void** result) =>
        ((delegate* unmanaged[Stdcall]<void*, void**, int>)VTable(obj)[slot])(obj, result);

    private static int QueryInterface(void* obj, Guid iid, void** result) =>
        ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)VTable(obj)[0])(obj, &iid, result);

    private static void Release(void* obj)
    {
        if (obj != null) ((delegate* unmanaged[Stdcall]<void*, uint>)VTable(obj)[2])(obj);
    }
}
