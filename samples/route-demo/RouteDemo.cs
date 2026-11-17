using System;
using System.Diagnostics;

namespace FileMapper.Samples;

// Analysis fixture only. FileMapper parses this file; it never executes it.
public static class RouteDemo
{
    public static void ReviewDynamicProcess(string untrustedProgram)
    {
        Process.Start(untrustedProgram);
    }

    public static string NormalizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "untitled";
        return label.Trim().ToUpperInvariant();
    }
}
