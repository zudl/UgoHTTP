using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using NUnit.Framework;
using UgoHttp.Native;
using UnityEngine;

public class LayoutTests
{
#if UNITY_EDITOR_WIN
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern uint GetModuleFileNameW(IntPtr module, System.Text.StringBuilder path, uint size);
#else
    [DllImport("/usr/lib/libSystem.dylib")] static extern uint _dyld_image_count();
    [DllImport("/usr/lib/libSystem.dylib")] static extern IntPtr _dyld_get_image_name(uint index);
#endif

    [Test]
    public void PluginLoadsFromPackage()
    {
        Assert.That(UgoHttp.UgoClient.MonotonicMicroseconds, Is.GreaterThan(0));
        string loaded = null;
#if UNITY_EDITOR_WIN
        IntPtr module = GetModuleHandleW("ugo.dll");
        var path = new System.Text.StringBuilder(1024);
        if (module != IntPtr.Zero && GetModuleFileNameW(module, path, (uint)path.Capacity) > 0) loaded = Path.GetFullPath(path.ToString());
        string expected = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../package/Plugins/Windows/x86_64/ugo.dll"));
#else
        for (uint i = 0, count = _dyld_image_count(); i < count; i++)
        {
            string image = Marshal.PtrToStringAnsi(_dyld_get_image_name(i));
            if (image.EndsWith("/ugo.bundle/Contents/MacOS/ugo", StringComparison.Ordinal)) loaded = image;
        }
        string expected = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../package/Plugins/macOS/ugo.bundle/Contents/MacOS/ugo"));
#endif
        UnityEngine.Debug.Log("UGO-PLUGIN " + loaded);
        Assert.That(loaded, Is.EqualTo(expected));
    }

    [Test]
    public void MonoLayoutMatchesNativeDump()
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
#if UNITY_EDITOR_WIN
        // Windows cannot start a shebang script.
        var start = new ProcessStartInfo("sh", "\"" + Path.Combine(root, "native/abi/dump") + "\"")
#else
        var start = new ProcessStartInfo(Path.Combine(root, "native/abi/dump"))
#endif
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        string native, error;
        using (Process dump = Process.Start(start))
        {
            var stderr = dump.StandardError.ReadToEndAsync();
            native = dump.StandardOutput.ReadToEnd();
            dump.WaitForExit();
            error = stderr.Result;
            Assert.That(dump.ExitCode, Is.Zero, error);
        }
        string managed = string.Join("\n", UgoAbi.Dump()) + "\n";
        UnityEngine.Debug.Log("UGO-MONO-LAYOUT\n" + managed);
        Assert.That(managed, Is.EqualTo(native.Replace("\r\n", "\n")));
    }
}
