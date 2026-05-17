using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;

namespace FluxProDisplay.HardwareMonitorHelper;

internal static class Program
{
    private const string PipeName = "FluxProDisplay_HardwareMonitor";

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath) : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path != null) return LoadFromAssemblyPath(path);
            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path != null) return LoadUnmanagedDllFromPath(path);
            return IntPtr.Zero;
        }
    }

    public static void Main()
    {
        // continuous server loop; each iteration will try to load the latest LibreHardwareMonitor DLL
        while (true)
        {
            try
            {
                var dllPath = FindLibreHardwareMonitorDll();
                if (dllPath == null)
                {
                    // nothing to load yet; sleep and retry
                    Thread.Sleep(2000);
                    continue;
                }

                using var alc = new PluginLoadContext(dllPath);
                var asm = alc.LoadFromAssemblyPath(dllPath);

                // required type names
                var computerType = asm.GetType("LibreHardwareMonitor.Hardware.Computer", throwOnError: true);
                var updateVisitorType = asm.GetType("LibreHardwareMonitor.Hardware.UpdateVisitor", throwOnError: true);

                // create and initialize Computer
                var computer = Activator.CreateInstance(computerType)!;
                computerType.GetProperty("IsCpuEnabled")!.SetValue(computer, true);
                computerType.GetProperty("IsGpuEnabled")!.SetValue(computer, true);
                computerType.GetMethod("Open")!.Invoke(computer, null);

                // Accept an initial update
                var updateVisitorInstance = Activator.CreateInstance(updateVisitorType);
                computerType.GetMethod("Accept")!.Invoke(computer, new[] { updateVisitorInstance });

                // Serve a single client connection, then unload and loop to pick up updated DLLs
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                server.WaitForConnection();

                try
                {
                    using var sr = new StreamReader(server);
                    using var sw = new StreamWriter(server) { AutoFlush = true };

                    var request = sr.ReadLine();
                    if (!string.Equals(request, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        sw.WriteLine("ERR");
                    }
                    else
                    {
                        // Update and read sensors via reflection
                        computerType.GetMethod("Accept")!.Invoke(computer, new[] { Activator.CreateInstance(updateVisitorType) });

                        double? cpuTemp = null;
                        double? gpuTemp = null;

                        var hardwareEnumerable = (IEnumerable)computerType.GetProperty("Hardware")!.GetValue(computer)!;
                        foreach (var hardware in hardwareEnumerable)
                        {
                            try
                            {
                                var hwTypeObj = hardware.GetType().GetProperty("HardwareType")!.GetValue(hardware)!;
                                var hwTypeName = hwTypeObj.ToString();

                                // CPU
                                if (string.Equals(hwTypeName, "Cpu", StringComparison.OrdinalIgnoreCase))
                                {
                                    var sensors = (IEnumerable)hardware.GetType().GetProperty("Sensors")!.GetValue(hardware)!;
                                    foreach (var sensor in sensors)
                                    {
                                        var sTypeObj = sensor.GetType().GetProperty("SensorType")!.GetValue(sensor)!;
                                        if (string.Equals(sTypeObj.ToString(), "Temperature", StringComparison.OrdinalIgnoreCase))
                                        {
                                            var val = sensor.GetType().GetProperty("Value")!.GetValue(sensor);
                                            if (val != null) cpuTemp = Convert.ToDouble(val, CultureInfo.InvariantCulture);
                                            break;
                                        }
                                    }
                                }

                                // GPU
                                if (string.Equals(hwTypeName, "GpuNvidia", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(hwTypeName, "GpuAmd", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(hwTypeName, "GpuIntel", StringComparison.OrdinalIgnoreCase))
                                {
                                    var sensors = (IEnumerable)hardware.GetType().GetProperty("Sensors")!.GetValue(hardware)!;
                                    foreach (var sensor in sensors)
                                    {
                                        var sTypeObj = sensor.GetType().GetProperty("SensorType")!.GetValue(sensor)!;
                                        var nameObj = sensor.GetType().GetProperty("Name")!.GetValue(sensor) as string ?? string.Empty;
                                        if (string.Equals(sTypeObj.ToString(), "Temperature", StringComparison.OrdinalIgnoreCase) &&
                                            nameObj.IndexOf("GPU Core", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            var val = sensor.GetType().GetProperty("Value")!.GetValue(sensor);
                                            if (val != null) gpuTemp = Convert.ToDouble(val, CultureInfo.InvariantCulture);
                                            break;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // ignore per-hardware sensor read failures
                            }
                        }

                        var cpuStr = cpuTemp.HasValue ? cpuTemp.Value.ToString("R", CultureInfo.InvariantCulture) : "null";
                        var gpuStr = gpuTemp.HasValue ? gpuTemp.Value.ToString("R", CultureInfo.InvariantCulture) : "null";
                        sw.WriteLine($"{cpuStr};{gpuStr}");
                    }
                }
                catch
                {
                    // client disconnect / protocol error: continue to unload and restart
                }
                finally
                {
                    // close and cleanup the computer instance before unloading
                    try { computerType.GetMethod("Close")!.Invoke(computer, null); } catch { }
                    // allow unload
                    alc.Unload();
                    // give GC a chance to collect loaded assembly references so file locks are released
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(200); // small delay to stabilize
                }
            }
            catch
            {
                // error loading DLL or runtime failure: wait and retry
                Thread.Sleep(1000);
            }
        }
    }

    private static string? FindLibreHardwareMonitorDll()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = Directory.EnumerateFiles(baseDir, "LibreHardwareMonitor*.dll", SearchOption.TopDirectoryOnly)
            .Concat(Directory.Exists(Path.Combine(baseDir, "rhm"))
                ? Directory.EnumerateFiles(Path.Combine(baseDir, "rhm"), "LibreHardwareMonitor*.dll", SearchOption.TopDirectoryOnly)
                : Enumerable.Empty<string>())
            .ToList();

        if (!candidates.Any()) return null;
        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
    }
}
