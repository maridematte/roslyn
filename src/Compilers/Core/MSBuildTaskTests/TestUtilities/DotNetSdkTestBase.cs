﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.BuildTasks.UnitTests
{
    public abstract partial class DotNetSdkTestBase : TestBase
    {
        public sealed class DotNetSdkAvailable : ExecutionCondition
        {
            public override bool ShouldSkip => s_dotnetSdkPath == null;
            public override string SkipReason => "The location of dotnet SDK can't be determined (DOTNET_INSTALL_DIR environment variable is unset)";
        }

        private static readonly string s_dotnetExeName;
        private static readonly string? s_dotnetInstallDir;
        private static readonly string s_dotnetSdkVersion;
        private static readonly string? s_dotnetSdkPath;

        private static readonly string s_projectSource =
@"<Project Sdk='Microsoft.NET.Sdk'>
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>
</Project>
";
        private static readonly string s_classSource =
@"using System;

public class TestClass 
{
    public void F() 
    {
        Console.WriteLine(123);
    }
}
";

        protected readonly ITestOutputHelper TestOutputHelper;
        protected readonly TempDirectory ProjectDir;
        protected readonly TempDirectory ObjDir;
        protected readonly TempDirectory OutDir;
        protected readonly TempFile Project;
        protected readonly string ProjectName;
        protected readonly string ProjectFileName;
        protected readonly string Configuration;
        protected readonly string TargetFramework;
        protected readonly string DotNetPath;
        protected readonly IReadOnlyDictionary<string, string> EnvironmentVariables;

        private int _logIndex;

        private static string GetSdkPath(string dotnetInstallDir, string version)
            => Path.Combine(dotnetInstallDir, "sdk", version);

        static DotNetSdkTestBase()
        {
            s_dotnetExeName = "dotnet" + (Path.DirectorySeparatorChar == '/' ? "" : ".exe");
            s_dotnetSdkVersion = typeof(DotNetSdkTests).Assembly.GetCustomAttribute<DotNetSdkVersionAttribute>()?.Version
                ?? throw new InvalidOperationException($"Couldn't find {nameof(DotNetSdkVersionAttribute)}");

            static bool isMatchingDotNetInstance(string? dotnetDir)
                => dotnetDir != null && File.Exists(Path.Combine(dotnetDir, s_dotnetExeName)) && Directory.Exists(GetSdkPath(dotnetDir, s_dotnetSdkVersion));

            var dotnetInstallDir = Environment.GetEnvironmentVariable("DOTNET_INSTALL_DIR");
            if (!isMatchingDotNetInstance(dotnetInstallDir))
            {
                dotnetInstallDir = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).FirstOrDefault(isMatchingDotNetInstance);
            }

            if (dotnetInstallDir != null)
            {
                s_dotnetInstallDir = dotnetInstallDir;
                s_dotnetSdkPath = GetSdkPath(dotnetInstallDir, s_dotnetSdkVersion);
            }
        }

        private const string EmptyValueMarker = "--{empty}--";

        private static void EmitTestHelperProps(
            string objDirectory,
            string projectFileName,
            string? content,
            ArtifactUploadUtil? uploadUtil)
        {
            // Common.props automatically import {project-name}.*.props files from MSBuildProjectExtensionsPath directory, 
            // which is by default set to the IntermediateOutputPath:
            var filePath = Path.Combine(objDirectory, projectFileName + ".TestHelpers.g.props");
            File.WriteAllText(filePath,
$@"<Project>
{content}
</Project>");
            uploadUtil?.AddFile(filePath);
        }

        private static void EmitTestHelperTargets(
            string objDirectory,
            string outputFile,
            string projectFileName,
            IEnumerable<string> expressions,
            string? additionalContent,
            ArtifactUploadUtil? uploadUtil)
        {
            // Common.targets automatically import {project-name}.*.targets files from MSBuildProjectExtensionsPath directory, 
            // which is by defautl set to the IntermediateOutputPath:
            var filePath = Path.Combine(objDirectory, projectFileName + ".TestHelpers.g.targets");
            File.WriteAllText(filePath,
$@"<Project>      
  <Target Name=""Test_EvaluateExpressions"">
    <PropertyGroup>
      {string.Join(Environment.NewLine + "      ", expressions.SelectWithIndex((e, i) => $@"<_Value{i}>{e}</_Value{i}><_Value{i} Condition=""'$(_Value{i})' == ''"">{EmptyValueMarker}</_Value{i}>"))}
    </PropertyGroup>
    <ItemGroup>
      <LinesToWrite Include=""{string.Join(";", expressions.SelectWithIndex((e, i) => $"$(_Value{i})"))}""/>
    </ItemGroup>
    <MakeDir Directories=""{Path.GetDirectoryName(outputFile)}"" />
    <WriteLinesToFile File=""{outputFile}""
                      Lines=""@(LinesToWrite)""
                      Overwrite=""true""
                      Encoding=""UTF-8"" />
  </Target>

  <!-- Overwrite CoreCompile target to avoid triggering the compiler -->
  <Target Name=""CoreCompile""
          DependsOnTargets=""$(CoreCompileDependsOn);_BeforeVBCSCoreCompile"">
  </Target>

  <Target Name=""InitializeSourceControlInformation""/>

{additionalContent}
</Project>");

            uploadUtil?.AddFile(filePath);
        }

        public DotNetSdkTestBase(ITestOutputHelper testOutputHelper)
        {
            Assert.True(s_dotnetInstallDir is object, $"SDK not found. Use {nameof(ConditionalFactAttribute)}(typeof({nameof(DotNetSdkAvailable)})) to skip the test if the SDK is not found.");
            Debug.Assert(s_dotnetInstallDir is object);

            DotNetPath = Path.Combine(s_dotnetInstallDir, s_dotnetExeName);
            var testBinDirectory = Path.GetDirectoryName(typeof(DotNetSdkTests).Assembly.Location) ?? string.Empty;
            var sdksDir = Path.Combine(s_dotnetSdkPath ?? string.Empty, "Sdks");

            TestOutputHelper = testOutputHelper;
            ProjectName = "test";
            ProjectFileName = ProjectName + ".csproj";
            Configuration = "Debug";
            TargetFramework = "netstandard2.0";

            using var uploadUtil = new ArtifactUploadUtil(testOutputHelper);
            ProjectDir = Temp.CreateDirectory();
            uploadUtil.AddDirectory(ProjectDir.Path);

            ObjDir = ProjectDir.CreateDirectory("obj");
            OutDir = ProjectDir.CreateDirectory("bin").CreateDirectory(Configuration).CreateDirectory(TargetFramework);

            Project = ProjectDir.CreateFile(ProjectFileName).WriteAllText(s_projectSource);
            ProjectDir.CreateFile("TestClass.cs").WriteAllText(s_classSource);

            // avoid accidental dependency on files outside of the project directory:
            ProjectDir.CreateFile("Directory.Build.props").WriteAllText("<Project/>");
            ProjectDir.CreateFile("Directory.Build.targets").WriteAllText("<Project/>");
            ProjectDir.CreateFile(".editorconfig").WriteAllText("root = true");

            var csharpCoreTargets = Path.Combine(testBinDirectory, "Microsoft.CSharp.Core.targets");
            var visualBasicCoreTargets = Path.Combine(testBinDirectory, "Microsoft.VisualBasic.Core.targets");

            Assert.True(File.Exists(csharpCoreTargets));
            Assert.True(File.Exists(visualBasicCoreTargets));

            EnvironmentVariables = new Dictionary<string, string>()
            {
                { "CSharpCoreTargetsPath", csharpCoreTargets },
                { "VisualBasicCoreTargetsPath", visualBasicCoreTargets },
                { "MSBuildSDKsPath", sdksDir },
                { "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", sdksDir }
            };

            var restoreResult = ProcessUtilities.Run(DotNetPath, $@"msbuild ""{Project.Path}"" /t:restore /bl:{Path.Combine(ProjectDir.Path, "restore.binlog")}",
                additionalEnvironmentVars: EnvironmentVariables);
            Assert.True(restoreResult.ExitCode == 0, $"Failed with exit code {restoreResult.ExitCode}: {restoreResult.Output}");

            Assert.True(File.Exists(Path.Combine(ObjDir.Path, "project.assets.json")));
            Assert.True(File.Exists(Path.Combine(ObjDir.Path, ProjectFileName + ".nuget.g.props")));
            Assert.True(File.Exists(Path.Combine(ObjDir.Path, ProjectFileName + ".nuget.g.targets")));
            uploadUtil.SetSucceeded();
        }

        protected void VerifyValues(string? customProps, string? customTargets, string[] targets, string[] expressions, string[] expectedResults)
        {
            using var uploadUtil = new ArtifactUploadUtil(TestOutputHelper);
            var evaluationResultsFile = Path.Combine(OutDir.Path, "EvaluationResult.txt");

            EmitTestHelperProps(ObjDir.Path, ProjectFileName, customProps, uploadUtil);
            EmitTestHelperTargets(ObjDir.Path, evaluationResultsFile, ProjectFileName, expressions, customTargets, uploadUtil);

            var targetsArg = string.Join(";", targets.Concat(new[] { "Test_EvaluateExpressions" }));
            var testBinDirectory = Path.GetDirectoryName(typeof(DotNetSdkTests).Assembly.Location);
            var binLog = Path.Combine(ProjectDir.Path, $"build{_logIndex++}.binlog");
            uploadUtil.AddFile(binLog);

            // RoslynTargetsPath is a path to the built-in Roslyn compilers in the .NET SDK.
            // For testing we are using compilers from custom location (this emulates usage of Microsoft.Net.Compilers package.
            // The core targets should be imported from CSharpCoreTargetsPath and VisualBasicCoreTargetsPath and the compiler tasks from the same location.
            var buildResult = ProcessUtilities.Run(DotNetPath, $@"msbuild ""{Project.Path}"" /t:{targetsArg} /p:RoslynTargetsPath=""<nonexistent directory>"" /p:Configuration={Configuration} /bl:""{binLog}""",
                additionalEnvironmentVars: EnvironmentVariables);
            Assert.True(buildResult.ExitCode == 0, $"Failed with exit code {buildResult.ExitCode}: {buildResult.Output}");

            var evaluationResult = File.ReadAllLines(evaluationResultsFile).Select(l => (l != EmptyValueMarker) ? l : "");
            AssertEx.Equal(expectedResults, evaluationResult);

            uploadUtil.SetSucceeded();
        }
    }
}
