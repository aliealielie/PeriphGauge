#requires -Version 7.6
$ErrorActionPreference = 'Stop'
$reader = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../ReceiverBattery.cs') -Raw -Encoding UTF8

Add-Type -CompilerOptions '/nullable:enable' -TypeDefinition ($reader + @"

public static class ReceiverBatterySmoke
{
    public static void Run()
    {
        byte[] mouse = new byte[65];
        Convert.FromHexString("00A100020200830051").CopyTo(mouse, 0);
        Check(ReceiverBattery.ParseMouse(mouse) == (81, false), "A5 percentage without charging");
        mouse[7] = 2;
        Check(ReceiverBattery.ParseMouse(mouse) == (81, true), "A5 nonzero charging byte");
        mouse[8] = 0;
        Check(ReceiverBattery.ParseMouse(mouse) == (0, true), "A5 charging at a valid 0 percent");
        mouse[7] = 0;
        Check(ReceiverBattery.ParseMouse(mouse) == (0, false), "A5 discharging at a valid 0 percent");
        mouse[7] = 1;
        mouse[8] = 101;
        Check(ReceiverBattery.ParseMouse(mouse) is null, "A5 impossible percentage");
        mouse[8] = 81;
        Check(ReceiverBattery.ParseMouse(mouse.AsSpan(0, 64)) is null, "A5 short packet");
        foreach (int index in new[] { 0, 1, 5, 6 })
        {
            mouse[index] ^= 1;
            Check(ReceiverBattery.ParseMouse(mouse) is null, "A5 invalid packet headers");
            mouse[index] ^= 1;
        }

        byte[] keyboard = Convert.FromHexString("134A010002340100000000000000000000000095");
        Check(ReceiverBattery.ParseKeyboard(keyboard) == 52, "Compx valid percentage");
        keyboard[^1] ^= 1;
        Check(ReceiverBattery.ParseKeyboard(keyboard) is null, "Compx incorrect checksum");
    }

    private static void Check(bool result, string explanation)
    {
        if (!result)
            throw new InvalidOperationException(explanation);
    }
}
"@)
[WirelessBattery.ReceiverBatterySmoke]::Run()
'Receiver battery packet smoke passed.'
