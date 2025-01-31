﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Microsoft.DotNet.ScenarioTests.Common;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Immutable;

namespace ScenarioTests
{
    public class SingleFileTestRunner : XunitTestFramework
    {
        private SingleFileTestRunner(IMessageSink messageSink)
        : base(messageSink) { }

        public static async Task<int> Main(string[] args)
        {
            var rootCommand = new CliRootCommand("Scenario test runner");

            CliOption<bool> listTestsOption = new("--list")
            {
                Description = "List tests that would be run, without running them."
            };
            CliOption<List<string>> noTraitsOption = new("--no-traits")
            {
                Description = "Do not run tests with the following traits. Format X=Y"
            };
            CliOption<List<string>> traitsOption = new("--traits")
            {
                Description = "Only run tests with the following traits. Format X=Y"
            };
            CliOption<bool> offlineOnlyOption = new("--offline-only")
            {
                Description = "Only run tests that can be run in offline mode. Implies --notraits 'resources=online'"
            };
            CliOption<string> xmlResultsPathOption = new("--xml")
            {
                Description = "XML result file."
            };
            CliOption<string> testRootOption = new("--test-root")
            {
                DefaultValueFactory = (_) => Directory.CreateTempSubdirectory().FullName,
                Description = "Directory used for temporary files when running tests"
            };
            CliOption<bool> noCleanTestRoot = new("--no-cleanup")
            {
                Description = "Do not cleanup the test root after execution."
            };

            CliOption<string> dotnetRootOption = new("--dotnet-root")
            {
                Description = "dotnet root to run tests against.",
                Required = true
            };
            dotnetRootOption.Validators.Add(v =>
            {
                string hostName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
                if (!File.Exists(Path.Combine(v.GetValue(dotnetRootOption)!, hostName)))
                {
                    v.AddError($"--dotnet-root must point to a valid dotnet root with host {hostName}");
                }
            });

            CliOption<string> sdkVersionOption = new("--sdk-version")
            {
                Description = "Version of SDK to use to run tests against. Optional. Otherwise uses the default SDK at the dotnet root."
            };

            CliOption<string> targetRidOption = new("--target-rid")
            {
                Description = "Target rid for tests requiring one (e.g. self-contained publish). If omitted, uses the target rid of the executing application",
                DefaultValueFactory = (_) => RuntimeInformation.RuntimeIdentifier
            };

            rootCommand.Options.Add(dotnetRootOption);
            rootCommand.Options.Add(testRootOption);
            rootCommand.Options.Add(sdkVersionOption);
            rootCommand.Options.Add(listTestsOption);
            rootCommand.Options.Add(offlineOnlyOption);
            rootCommand.Options.Add(noTraitsOption);
            rootCommand.Options.Add(traitsOption);
            rootCommand.Options.Add(xmlResultsPathOption);
            rootCommand.Options.Add(noCleanTestRoot);
            rootCommand.Options.Add(targetRidOption);


            rootCommand.SetAction((ParseResult parseResult) =>
            {
                Invoke(parseResult.GetValue(dotnetRootOption)!,
                       parseResult.GetValue(testRootOption)!,
                       parseResult.GetValue(sdkVersionOption),
                       parseResult.GetValue(targetRidOption)!,
                       parseResult.GetValue(listTestsOption),
                       parseResult.GetValue(offlineOnlyOption),
                       parseResult.GetValue(noTraitsOption) ?? (IList<string>)ImmutableList<string>.Empty,
                       parseResult.GetValue(traitsOption) ?? (IList<string>)ImmutableList<string>.Empty,
                       parseResult.GetValue(xmlResultsPathOption),
                       parseResult.GetValue(noCleanTestRoot));
            });

            return await rootCommand.Parse(args).InvokeAsync();
        }

        public static int Invoke(string dotnetRoot,
                                 string testRoot,
                                 string? sdkVersion,
                                 string targetRid,
                                 bool listOnly,
                                 bool offlineOnly,
                                 IList<string> noTraits,
                                 IList<string> traits,
                                 string? xmlResultsPath,
                                 bool noCleanTestRoot)
        {
            var asm = typeof(SingleFileTestRunner).Assembly;

            var diagnosticSink = new ConsoleDiagnosticMessageSink();
            var testsFinished = new TaskCompletionSource();
            var testSink = new TestMessageSink();
            var summarySink = new DelegatingExecutionSummarySink(testSink,
                () => false,
                (completed, summary) => Console.WriteLine($"Tests run: {summary.Total}, Errors: {summary.Errors}, Failures: {summary.Failed}, Skipped: {summary.Skipped}. Time: {TimeSpan.FromSeconds((double)summary.Time).TotalSeconds}s"));
            var resultsXmlAssembly = new XElement("assembly");
            var resultsSink = new DelegatingXmlCreationSink(summarySink, resultsXmlAssembly);

            testSink.Execution.TestSkippedEvent += args => { Console.WriteLine($"[SKIP] {args.Message.Test.DisplayName}"); };
            testSink.Execution.TestFailedEvent += args => { Console.WriteLine($"[FAIL] {args.Message.Test.DisplayName}{Environment.NewLine}{Xunit.ExceptionUtility.CombineMessages(args.Message)}{Environment.NewLine}{Xunit.ExceptionUtility.CombineStackTraces(args.Message)}"); };

            testSink.Execution.TestAssemblyFinishedEvent += args =>
            {
                Console.WriteLine($"Finished {args.Message.TestAssembly.Assembly}{Environment.NewLine}");
                testsFinished.SetResult();
            };

            var assemblyConfig = new TestAssemblyConfiguration()
            {
                // Turn off pre-enumeration of theories, since there is no theory selection UI in this runner
                PreEnumerateTheories = false,
            };

            var xunitTestFx = new SingleFileTestRunner(diagnosticSink);
            var asmInfo = Reflector.Wrap(asm);
            var asmName = asm.GetName();

            var discoverySink = new TestDiscoverySink();
            var discoverer = xunitTestFx.CreateDiscoverer(asmInfo);
            discoverer.Find(false, discoverySink, TestFrameworkOptions.ForDiscovery(assemblyConfig));
            discoverySink.Finished.WaitOne();

            XunitFilters filters = CreateFilters(noTraits, traits, offlineOnly);

            var filteredTestCases = discoverySink.TestCases.Where(filters.Filter).ToList();

            Console.WriteLine("Test environment:");
            Console.WriteLine($"  Dotnet Root: {dotnetRoot}");
            Console.WriteLine($"  Test root: {testRoot}");
            Console.WriteLine($"  Target RID: {targetRid}");
            Console.WriteLine($"  Sdk Version: {sdkVersion ?? "latest"}");

            if (listOnly)
            {
                Console.WriteLine("Tests to execute:");
                foreach (var test in filteredTestCases)
                {
                    string testTraits = "";
                    foreach (var traitKey in test.Traits.Keys)
                    {
                        testTraits = $" {traitKey}={string.Join(", ", test.Traits[traitKey])}";
                    }
                    Console.WriteLine($"{test.DisplayName}{testTraits}");
                }
                return 0;
            }

            SetupTestEnvironment(dotnetRoot, testRoot, sdkVersion, targetRid);

            var executor = xunitTestFx.CreateExecutor(asmName);
            executor.RunTests(filteredTestCases, resultsSink, TestFrameworkOptions.ForExecution(assemblyConfig));

            resultsSink.Finished.WaitOne();

            if (xmlResultsPath != null)
            {
                resultsXmlAssembly.Save(xmlResultsPath); 
            }

            if (!noCleanTestRoot)
            {
                Directory.Delete(testRoot, true);
            }

            var failed = resultsSink.ExecutionSummary.Failed > 0 || resultsSink.ExecutionSummary.Errors > 0;
            return failed ? 1 : 0;
        }

        private static void SetupTestEnvironment(string dotnetRoot, string testRoot, string? sdkVersion, string targetRid)
        {
            // Verify that the input parameters 
            // Create any directories as necessary
            Directory.CreateDirectory(testRoot);

            // Set up environment variables based on the input data
            Environment.SetEnvironmentVariable(ScenarioTestFixture.DotNetRootEnvironmentVariable, dotnetRoot);
            Environment.SetEnvironmentVariable(ScenarioTestFixture.TestRootEnvironmentVariable, testRoot);
            Environment.SetEnvironmentVariable(ScenarioTestFixture.SdkVersionEnvironmentVariable, sdkVersion);
            Environment.SetEnvironmentVariable(ScenarioTestFixture.TargetRidEnvironmentVariable, targetRid);
        }


        private static XunitFilters CreateFilters(IList<string> excludedTraits, IList<string> includedTraits, bool offlineOnly)
        {
            XunitFilters filters = new XunitFilters();

            if (offlineOnly)
            {
                filters.IncludedTraits.Add("Category", new List<string>() { "Offline" });
            }

            Dictionary<string, List<string>> excludedTraitsMap = ParseTraitKeyValuePairs(excludedTraits);
            foreach (KeyValuePair<string, List<string>> kvp in excludedTraitsMap)
            {
                filters.ExcludedTraits.Add(kvp.Key, kvp.Value);
            }

            Dictionary<string, List<string>> includedTraitsMap = ParseTraitKeyValuePairs(includedTraits);
            foreach (KeyValuePair<string, List<string>> kvp in excludedTraitsMap)
            {
                filters.IncludedTraits.Add(kvp.Key, kvp.Value);
            }

            return filters;
        }

        private static Dictionary<string, List<string>> ParseTraitKeyValuePairs(IList<string> excludedTraits)
        {
            // Quick hack wo much validation to get args that are passed (notrait, xml)
            Dictionary<string, List<string>> excludedTraitsMap = new Dictionary<string, List<string>>();
            for (int i = 0; i < excludedTraits.Count(); i++)
            {
                var traitKeyValue = excludedTraits[i + 1].Split("=", StringSplitOptions.TrimEntries);
                if (!excludedTraitsMap.TryGetValue(traitKeyValue[0], out List<string>? values))
                {
                    excludedTraitsMap.Add(traitKeyValue[0], values = new List<string>());
                }
                values.Add(traitKeyValue[1]);
            }

            return excludedTraitsMap;
        }
    }

    internal class ConsoleDiagnosticMessageSink : IMessageSink
    {
        public bool OnMessage(IMessageSinkMessage message)
        {
            if (message is IDiagnosticMessage diagnosticMessage)
            {
                return true;
            }
            return false;
        }
    }
}