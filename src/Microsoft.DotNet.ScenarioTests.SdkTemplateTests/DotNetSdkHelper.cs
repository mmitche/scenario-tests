﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.DotNet.ScenarioTests.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace Microsoft.DotNet.ScenarioTests.SdkTemplateTests;

internal class DotNetSdkHelper
{
    public string DotNetRoot { get; set; }
    public string? SdkVersion { get; set; }
    public string DotNetExecutablePath { get =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Path.Combine(DotNetRoot, "dotnet.exe") : Path.Combine(DotNetRoot, "dotnet"); }

    private ITestOutputHelper OutputHelper { get; }

    public DotNetSdkHelper(ITestOutputHelper outputHelper, string dotnetRoot, string? sdkVersion)
    {
        OutputHelper = outputHelper;
        DotNetRoot = dotnetRoot;
        SdkVersion = sdkVersion;
    }

    private void ExecuteCmd(string args, string workingDirectory, Action<Process>? additionalProcessConfigCallback = null, int expectedExitCode = 0, int millisecondTimeout = -1)
    {
        if (!string.IsNullOrEmpty(SdkVersion) && !File.Exists(Path.Combine(workingDirectory, "global.json")))
        {
            ExecuteCmdImpl($"new globaljson --sdk-version {SdkVersion}", workingDirectory);
        }

        ExecuteCmdImpl(args, workingDirectory, additionalProcessConfigCallback, expectedExitCode, millisecondTimeout);
    }

    private void ExecuteCmdImpl(string args, string workingDirectory, Action<Process>? additionalProcessConfigCallback = null, int expectedExitCode = 0, int millisecondTimeout = -1)
    {
        (Process Process, string StdOut, string StdErr) executeResult = ExecuteHelper.ExecuteProcess(
            DotNetExecutablePath,
            args,
            OutputHelper,
            configure: (process) => configureProcess(process, workingDirectory),
            millisecondTimeout: millisecondTimeout);

        ExecuteHelper.ValidateExitCode(executeResult, expectedExitCode);

        void configureProcess(Process process, string workingDirectory)
        {
            ConfigureProcess(process, workingDirectory, DotNetRoot, nugetPackagesDirectory: null, setPath: false);

            additionalProcessConfigCallback?.Invoke(process);
        }
    }

    private static void ConfigureProcess(Process process, string workingDirectory, string dotnetRoot, string? nugetPackagesDirectory = null, bool setPath = false, bool clearEnv = false)
    {
        process.StartInfo.WorkingDirectory = workingDirectory;

        // The `dotnet test` execution context sets a number of dotnet related ENVs that cause issues when executing
        // dotnet commands.  Clear these to avoid side effects.

        foreach (string key in process.StartInfo.Environment.Keys.Where(key => key.StartsWith("DOTNET_")).ToList())
        {
            process.StartInfo.Environment.Remove(key);
        }

        process.StartInfo.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        process.StartInfo.EnvironmentVariables["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        process.StartInfo.EnvironmentVariables["DOTNET_ROOT"] = dotnetRoot;
        if (!string.IsNullOrEmpty(nugetPackagesDirectory))
        {
            process.StartInfo.EnvironmentVariables["NUGET_PACKAGES"] = nugetPackagesDirectory;
        }

        if (setPath)
        {
            process.StartInfo.EnvironmentVariables["PATH"] = $"{dotnetRoot}:{Environment.GetEnvironmentVariable("PATH")}";
        }
    }

    public void ExecuteBuild(string projectDirectory) =>
        ExecuteCmd($"build {GetBinLogOption(projectDirectory, "build")}", projectDirectory);

    /// <summary>
    /// Create a new .NET project and return the path to the created project folder.
    /// </summary>
    public string ExecuteNew(string projectType, string projectName, string projectDirectory, string? language = null, string? customArgs = null)
    {
        string options = $"--name {projectName} --output {projectDirectory}";
        if (language != null)
        {
            options += $" --language \"{language}\"";
        }
        if (string.IsNullOrEmpty(customArgs))
        {
            options += $" {customArgs}";
        }

        ExecuteCmd($"new {projectType} {options}", projectDirectory);

        return projectDirectory;
    }

    public void ExecutePublish(string projectDirectory, bool? selfContained = null, string? rid = null, bool trimmed = false, bool readyToRun = false)
    {
        string options = string.Empty;
        string binlogDifferentiator = string.Empty;

        if (selfContained.HasValue)
        {
            options += $"--self-contained {selfContained.Value.ToString().ToLowerInvariant()}";
            if (selfContained.Value)
            {
                binlogDifferentiator += "self-contained";
                if (!string.IsNullOrEmpty(rid))
                {
                    options += $" -r {rid}";
                    binlogDifferentiator += $"-{rid}";
                }
                if (trimmed)
                {
                    options += " /p:PublishTrimmed=true";
                    binlogDifferentiator += "-trimmed";
                }
                if (readyToRun)
                {
                    options += " /p:PublishReadyToRun=true";
                    binlogDifferentiator += "-R2R";
                }
            }
        }

        ExecuteCmd(
            args: $"publish {options} {GetBinLogOption(projectDirectory, "publish", binlogDifferentiator)}",
            workingDirectory: projectDirectory);
    }

    public void ExecuteRun(string projectDirectory) =>
        ExecuteCmd($"run {GetBinLogOption(projectDirectory, "run")}", projectDirectory);

    public void ExecuteRunWeb(string projectDirectory)
    {
        ExecuteCmd(
            $"run {GetBinLogOption(projectDirectory, "run")}",
            projectDirectory,
            additionalProcessConfigCallback: processConfigCallback,
            millisecondTimeout: 30000);

        void processConfigCallback(Process process)
        {
            process.OutputDataReceived += new DataReceivedEventHandler((sender, e) =>
            {
                if (e.Data?.Contains("Application started. Press Ctrl+C to shut down.") ?? false)
                {
                    ExecuteHelper.ExecuteProcessValidateExitCode("kill", $"-s TERM {process.Id}", OutputHelper);
                }
            });
        }
    }

    public void ExecuteTest(string projectDirectory) =>
        ExecuteCmd($"test {GetBinLogOption(projectDirectory, "test")}", workingDirectory: projectDirectory);

    private static string GetBinLogOption(string projectDirectory, string command, string? differentiator = null)
    {
        string fileName = $"{command}";
        if (!string.IsNullOrEmpty(differentiator))
        {
            fileName += $"-{differentiator}";
        }

        return $"/bl:{Path.Combine(projectDirectory, $"{fileName}.binlog")}";
    }
}
