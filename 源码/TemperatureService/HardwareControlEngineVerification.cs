#nullable enable
using System;
using System.Collections.Generic;

namespace FileDockTemperature;

internal static class HardwareControlEngineVerification
{
    internal static void Run(Action<bool, string> check)
    {
        var wmi = new FakeLenovoWmi();
        var engine = new HardwareControlEngine(wmi);

        check(engine.HandleCommand("PING") == "OK|PONG|v1", "Hardware service replies to PING");

        string readReply = engine.HandleCommand("READ");
        check(readReply.StartsWith("OK|STATE|1|Lenovo Legion WMI|2|1|1|2400|2600|5200|1|0|", StringComparison.Ordinal) &&
              readReply.Contains("0102FF00,cpu-pl1,30,115,5,65,65") &&
              readReply.Contains("0202FF00,gpu-ctgp,55,140,5,115,115"),
            "Hardware service reads Lenovo mode, fan speeds and BIOS capability limits");

        check(engine.HandleCommand("SET_FEATURE 0102FF00 80").StartsWith("ERR|", StringComparison.Ordinal) && wmi.Writes == 0,
            "Hardware service rejects custom power writes outside Custom mode");

        wmi.OnAc = false;
        check(engine.HandleCommand("SET_MODE 255").StartsWith("ERR|", StringComparison.Ordinal) &&
              engine.HandleCommand("SET_MODE 3").StartsWith("ERR|", StringComparison.Ordinal) &&
              wmi.Mode == 2,
            "Hardware service blocks Performance and Custom modes while on battery");

        wmi.OnAc = true;
        check(engine.HandleCommand("SET_MODE 255").StartsWith("OK|STATE|", StringComparison.Ordinal) && wmi.Mode == 255,
            "Hardware service switches to Custom mode on AC power");

        check(engine.HandleCommand("SET_FEATURE 0102FF00 120").StartsWith("ERR|", StringComparison.Ordinal) &&
              engine.HandleCommand("SET_FEATURE 0102FF00 25").StartsWith("ERR|", StringComparison.Ordinal) &&
              engine.HandleCommand("SET_FEATURE 0102FF00 67").StartsWith("ERR|", StringComparison.Ordinal) &&
              engine.HandleCommand("SET_FEATURE DEADBEEF 1").StartsWith("ERR|", StringComparison.Ordinal) &&
              wmi.Writes == 0,
            "Hardware service rejects out-of-range, misaligned-step and non-allowlisted feature writes");

        check(engine.HandleCommand("SET_FEATURE 0102FF00 85").StartsWith("OK|STATE|", StringComparison.Ordinal) &&
              wmi.Values[0x0102FF00] == 85 &&
              engine.HandleCommand("SET_FEATURE 04020000 1").StartsWith("OK|STATE|", StringComparison.Ordinal) &&
              wmi.Values[0x04020000] == 1,
            "Hardware service applies validated BIOS-bounded power limit and full-speed fan writes");

        string reset = engine.HandleCommand("RESET_DEFAULTS");
        check(reset.StartsWith("OK|STATE|", StringComparison.Ordinal) &&
              wmi.Mode == 2 && wmi.Values[0x0102FF00] == 65 && wmi.Values[0x04020000] == 0,
            "Reset defaults restores BIOS default values, clears full-speed fan and returns to Balance mode");

        check(engine.HandleCommand("UNKNOWN_CMD 1 2").StartsWith("ERR|", StringComparison.Ordinal) &&
              engine.HandleCommand("SET_MODE 99").StartsWith("ERR|", StringComparison.Ordinal),
            "Hardware service rejects malformed commands and invalid mode codes");
    }

    private sealed class FakeLenovoWmi : ILenovoWmiTransport
    {
        internal bool OnAc = true;
        internal int Mode = 2;
        internal int Writes;
        internal readonly Dictionary<uint, int> Values = new()
        {
            [0x0102FF00] = 65,
            [0x0202FF00] = 115,
            [0x04020000] = 0
        };

        public bool HasGameZone() => true;
        public bool IsAcPowerConnected() => OnAc;
        public int? GetSmartFanMode() => Mode;
        public void SetSmartFanMode(int mode) => Mode = mode;
        public int? GetFan1Speed() => 2400;
        public int? GetFan2Speed() => 2600;
        public int? GetFanMaxSpeed() => 5200;

        public IReadOnlyList<(uint Id, int Min, int Max, int Step, int DefaultValue)> ReadCapabilityData01() =>
            new (uint, int, int, int, int)[]
            {
                (0x0102FF00, 30, 115, 5, 65),
                (0x0202FF00, 55, 140, 5, 115)
            };

        public int? GetFeatureValue(uint id) => Values.TryGetValue(id, out int v) ? v : null;
        public void SetFeatureValue(uint id, int value) { Writes++; Values[id] = value; }
    }
}
