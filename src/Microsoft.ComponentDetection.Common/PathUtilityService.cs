﻿namespace Microsoft.ComponentDetection.Common;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.ComponentDetection.Contracts;
using Microsoft.Win32.SafeHandles;

// We may want to consider breaking this class into Win/Mac/Linux variants if it gets bigger
public class PathUtilityService : IPathUtilityService
{
    public const uint CreationDispositionRead = 0x3;

    public const uint FileFlagBackupSemantics = 0x02000000;

    public const int InitalPathBufferSize = 512;

    public const string LongPathPrefix = "\\\\?\\";

    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    private static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private readonly ConcurrentDictionary<string, string> resolvedPaths = new ConcurrentDictionary<string, string>();

    private readonly ILogger logger;

    private readonly object isRunningOnWindowsContainerLock = new object();
    private bool? isRunningOnWindowsContainer;

    public PathUtilityService()
    {
    }

    public PathUtilityService(ILogger logger) => this.logger = logger;

    public bool IsRunningOnWindowsContainer
    {
        get
        {
            if (!this.isRunningOnWindowsContainer.HasValue)
            {
                lock (this.isRunningOnWindowsContainerLock)
                {
                    if (!this.isRunningOnWindowsContainer.HasValue)
                    {
                        this.isRunningOnWindowsContainer = this.CheckIfRunningOnWindowsContainer();
                    }
                }
            }

            return this.isRunningOnWindowsContainer.Value;
        }
    }

    /// <summary>
    /// This call can be made on a linux system to get the absolute path of a file. It will resolve nested layers.
    /// Note: You may pass IntPtr.Zero to the output parameter. You MUST then free the IntPtr that RealPathLinux returns
    /// using FreeMemoryLinux otherwise things will get very leaky.
    /// </summary>
    /// <param name="path"> The path to resolve. </param>
    /// <param name="output"> The pointer output. </param>
    /// <returns> A pointer <see cref= "IntPtr"/> to the absolute path of a file. </returns>
    [DllImport("libc", EntryPoint = "realpath")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
#pragma warning disable CA2101 // Specify marshaling for P/Invoke string arguments. libc expects a null-terminated ANSI string.
    private static extern IntPtr RealPathLinux([MarshalAs(UnmanagedType.LPStr)] string path, IntPtr output);
#pragma warning restore CA2101 // Specify marshaling for P/Invoke string arguments

    /// <summary>
    /// Use this function to free memory and prevent memory leaks.
    /// However, beware.... Improper usage of this function will cause segfaults and other nasty double-free errors.
    /// THIS WILL CRASH THE CLR IF YOU USE IT WRONG.
    /// </summary>
    /// <param name="toFree">Pointer to the memory space to free. </param>
    [DllImport("libc", EntryPoint = "free")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void FreeMemoryLinux([In] IntPtr toFree);

    public static bool MatchesPattern(string searchPattern, ref FileSystemEntry fse)
    {
        if (searchPattern.StartsWith("*") && fse.FileName.EndsWith(searchPattern.AsSpan()[1..], StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        else if (searchPattern.EndsWith("*") && fse.FileName.StartsWith(searchPattern.AsSpan()[..^1], StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        else if (fse.FileName.Equals(searchPattern.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    public string GetParentDirectory(string path)
    {
        return Path.GetDirectoryName(path);
    }

    public bool IsFileBelowAnother(string aboveFilePath, string belowFilePath)
    {
        var aboveDirectoryPath = Path.GetDirectoryName(aboveFilePath);
        var belowDirectoryPath = Path.GetDirectoryName(belowFilePath);

        // Return true if they are not the same path but the second has the first as its base
        return (aboveDirectoryPath.Length != belowDirectoryPath.Length) && belowDirectoryPath.StartsWith(aboveDirectoryPath);
    }

    public bool MatchesPattern(string searchPattern, string fileName)
    {
        if (searchPattern.StartsWith("*") && fileName.EndsWith(searchPattern[1..], StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        else if (searchPattern.EndsWith("*") && fileName.StartsWith(searchPattern[..^1], StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        else if (searchPattern.Equals(fileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    public string ResolvePhysicalPath(string path)
    {
        if (IsWindows)
        {
            return this.ResolvePhysicalPathWindows(path);
        }
        else if (IsLinux || IsMacOS)
        {
            return this.ResolvePhysicalPathLibC(path);
        }

        return path;
    }

    public string ResolvePhysicalPathWindows(string path)
    {
        if (!IsWindows)
        {
            throw new PlatformNotSupportedException("Attempted to call a function that makes windows-only SDK calls");
        }

        if (this.IsRunningOnWindowsContainer)
        {
            return path;
        }

        if (this.resolvedPaths.TryGetValue(path, out var cachedPath))
        {
            return cachedPath;
        }

        var symlink = new DirectoryInfo(path);

        using var directoryHandle = CreateFile(symlink.FullName, 0, 2, IntPtr.Zero, CreationDispositionRead, FileFlagBackupSemantics, IntPtr.Zero);

        if (directoryHandle.IsInvalid)
        {
            return path;
        }

        var resultBuf = new char[InitalPathBufferSize];
        var mResult = GetFinalPathNameByHandle(directoryHandle.DangerousGetHandle(), resultBuf, InitalPathBufferSize, 0);

        // If GetFinalPathNameByHandle needs a bigger buffer, it will tell us the size it needs (including the null terminator) in finalPathNameResultCode
        if (mResult > InitalPathBufferSize)
        {
            resultBuf = new char[mResult];
            mResult = GetFinalPathNameByHandle(directoryHandle.DangerousGetHandle(), resultBuf, mResult, 0);
        }

        if (mResult < 0)
        {
            return path;
        }

        var result = resultBuf.ToString();

        result = result.StartsWith(LongPathPrefix) ? result[LongPathPrefix.Length..] : result;

        this.resolvedPaths.TryAdd(path, result);

        return result;
    }

    public string ResolvePhysicalPathLibC(string path)
    {
        if (!IsLinux && !IsMacOS)
        {
            throw new PlatformNotSupportedException("Attempted to call a function that makes linux-only library calls");
        }

        var pointer = IntPtr.Zero;
        try
        {
            pointer = RealPathLinux(path, IntPtr.Zero);

            if (pointer != IntPtr.Zero)
            {
                var toReturn = Marshal.PtrToStringAnsi(pointer);
                return toReturn;
            }
            else
            {
                return path;
            }
        }
        catch (Exception ex)
        {
            this.logger.LogException(ex, isError: false, printException: true);
            return path;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                FreeMemoryLinux(pointer);
            }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(
        [In] string lpFileName,
        [In] uint dwDesiredAccess,
        [In] uint dwShareMode,
        [In] IntPtr lpSecurityAttributes,
        [In] uint dwCreationDisposition,
        [In] uint dwFlagsAndAttributes,
        [In] IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetFinalPathNameByHandle([In] IntPtr hFile, [Out] char[] lpszFilePath, [In] int cchFilePath, [In] int dwFlags);

    private bool CheckIfRunningOnWindowsContainer()
    {
        if (IsLinux)
        {
            return false;
        }

        // This isn't the best way to do this in C#, but netstandard doesn't seem to support the service api calls
        // that we need to do this without shelling out
        var process = new Process()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c NET START",
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
            },
        };

        var sb = new StringBuilder();
        process.Start();

        while (!process.HasExited)
        {
            sb.Append(process.StandardOutput.ReadToEnd());
        }

        process.WaitForExit();
        sb.Append(process.StandardOutput.ReadToEnd());

        if (sb.ToString().Contains("Container Execution Agent"))
        {
            this.logger.LogWarning("Detected execution in a Windows container. Currently windows containers < 1809 do not support symlinks well, so disabling symlink resolution/dedupe behavior");
            return true;
        }
        else
        {
            return false;
        }
    }
}
