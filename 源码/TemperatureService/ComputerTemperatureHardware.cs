using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace FileDockTemperature;

internal sealed class ComputerTemperatureHardware : ITemperatureHardware
{
    private readonly Computer _computer = new() { IsCpuEnabled = true };
    private IHardware[] _cpus = Array.Empty<IHardware>();

    public void Open()
    {
        _computer.Open();
        _cpus = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu).ToArray();
    }

    public double Read()
    {
        foreach (var cpu in _cpus) cpu.Update();
        var temperatures = _cpus.SelectMany(h => h.Sensors)
            .Where(s => s.SensorType == SensorType.Temperature && s.Value > 0 && s.Value <= 130).ToArray();
        var sensor = temperatures.FirstOrDefault(s => s.Name.IndexOf("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? temperatures.FirstOrDefault(s => s.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? temperatures.FirstOrDefault(s => s.Name.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? temperatures.FirstOrDefault();
        return sensor?.Value ?? double.NaN;
    }

    public void Close()
    {
        _cpus = Array.Empty<IHardware>();
        _computer.Close();
    }
}
