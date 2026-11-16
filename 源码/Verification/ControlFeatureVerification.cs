using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace FileDockDemo;

internal static class ControlFeatureVerification
{
    internal static async Task Run(MainWindow window,string directory)
    {
        Directory.CreateDirectory(directory);var report=new List<string>();
        void Check(bool success,string message){if(!success)throw new InvalidOperationException("FAIL: "+message);report.Add("PASS: "+message);}
        window.Show();window.SelectPage("Control",false);
        await ControlReviewRegression.Run(window,Check);
        await BrightnessVerification.Run(window,Check);await PowerVerification.Run(window,Check);await DisplayPowerViewVerification.Run(window,directory,Check);await AudioControlVerification.Run(window,directory,Check);
        File.WriteAllLines(Path.Combine(directory,"control-verification.txt"),report);
    }
}
