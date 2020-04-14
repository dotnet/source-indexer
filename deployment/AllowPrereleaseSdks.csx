using System;
using System.IO;

var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var vsroot = Path.Combine(localAppData, "Microsoft", "VisualStudio");
Directory.CreateDirectory(vsroot);
var hives = Directory.GetDirectories(vsroot, "16.0_*");

foreach (var hive in hives)
{
    var filePath = Path.Combine(hive, "sdk.txt");
    File.WriteAllText(filePath, "UsePreviews=True");
}
