using System;
using System.Collections.Generic;
using System.IO;

namespace FalconBMS.Launcher.Services;

public static class DeviceSuppressListService
{
    public static HashSet<Guid> Load(string baseDir)
    {
        var suppressedGuids = new HashSet<Guid>();

        string path = Path.Combine(
            baseDir,
            "User",
            "Config",
            "DeviceSuppress.txt");

        if (!File.Exists(path))
            return suppressedGuids;

        foreach (string line in File.ReadLines(path))
        {
            // The first 38 characters contain the GUID.
            // Text and comments after it are ignored.
            if (line.Length < 38)
                continue;

            if (Guid.TryParseExact(
                    line.Substring(0, 38),
                    "B",
                    out Guid guid))
            {
                suppressedGuids.Add(guid);
            }
        }

        return suppressedGuids;
    }
}