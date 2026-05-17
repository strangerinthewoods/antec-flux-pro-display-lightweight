using LibreHardwareMonitor.Hardware;

namespace FluxProDisplay;

public class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private IHardware? _cpuHardware;
    private ISensor? _cpuSensor;
    private IHardware? _gpuHardware;
    private ISensor? _gpuSensor;

    public HardwareMonitor()
    {
        _computer = new Computer()
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true
        };

        _computer.Open();
        _computer.Accept(new UpdateVisitor());
    }

    public void Dispose()
    {
        _computer.Close();
        GC.SuppressFinalize(this);
    }

    public float? GetCpuTemperature()
    {
        if (_cpuSensor == null) ResolveCpuSensor();
        if (_cpuSensor == null) return null;

        _cpuHardware!.Update();
        return _cpuSensor.Value;
    }

    public float? GetGpuTemperature()
    {
        if (_gpuSensor == null) ResolveGpuSensor();
        if (_gpuSensor == null) return null;

        _gpuHardware!.Update();
        return _gpuSensor.Value;
    }

    private void ResolveCpuSensor()
    {
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu) continue;

            hardware.Update();

            ISensor? firstTempSensor = null;
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature) continue;

                if (sensor.Name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) ||
                    sensor.Name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase))
                {
                    _cpuHardware = hardware;
                    _cpuSensor = sensor;
                    return;
                }

                firstTempSensor ??= sensor;
            }

            // fall back to any CPU temperature sensor (laptops, Intel E-core
            // parts, and older AMD chips don't always expose the preferred names)
            if (firstTempSensor != null)
            {
                _cpuHardware = hardware;
                _cpuSensor = firstTempSensor;
                return;
            }
        }
    }

    private void ResolveGpuSensor()
    {
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)) continue;

            hardware.Update();

            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType == SensorType.Temperature &&
                    sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase))
                {
                    _gpuHardware = hardware;
                    _gpuSensor = sensor;
                    return;
                }
            }
        }
    }
}