﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

namespace BinaryCompatChecker
{
    public partial class Checker
    {
        Dictionary<string, AssemblyDefinition> filePathToModuleDefinition = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, AssemblyDefinition> resolveCache = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<AssemblyDefinition, Dictionary<string, bool>> assemblyToTypeList = new();

        List<string> assembliesExamined = new();
        List<string> reportLines = new();
        List<IVTUsage> ivtUsages = new();
        HashSet<string> unresolvedAssemblies = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> diagnostics = new(StringComparer.OrdinalIgnoreCase);

        static CommandLine commandLine;

        [STAThread]
        static int Main(string[] args)
        {
            commandLine = CommandLine.Parse(args);
            if (commandLine == null)
            {
                return -1;
            }

            bool success = new Checker().Check();
            return success ? 0 : 1;
       }

        public static bool IsWindows { get; } = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

        public Checker()
        {
            resolver = new CustomAssemblyResolver(this);
        }

        /// <returns>true if the check succeeded, false if the report is different from the baseline</returns>
        public bool Check()
        {
            bool success = true;

            var appConfigFiles = new List<string>();

            Queue<string> fileQueue = new(commandLine.Files);

            HashSet<string> frameworkAssemblyNames = GetFrameworkAssemblyNames();
            HashSet<string> assemblyNamesToIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            assemblyNamesToIgnore.UnionWith(frameworkAssemblyNames);

            while (fileQueue.Count != 0)
            {
                string file = fileQueue.Dequeue();
                if (file.EndsWith(".exe.config", CommandLine.PathComparison) ||
                    file.EndsWith(".dll.config", CommandLine.PathComparison))
                {
                    appConfigFiles.Add(file);
                    continue;
                }

                var assemblyDefinition = Load(file);
                if (assemblyDefinition == null)
                {
                    continue;
                }

                WriteLine(file, color: ConsoleColor.DarkGray);
                WriteLine(assemblyDefinition.FullName, color: ConsoleColor.DarkCyan);

                if (IsNetFrameworkAssembly(assemblyDefinition))
                {
                    if (IsFacadeAssembly(assemblyDefinition))
                    {
                        var relativePath = GetRelativePath(file);
                        Log($"Facade assembly: {relativePath}");
                    }

                    continue;
                }

                // var relativePath = file.Substring(rootDirectory.Length + 1);
                // Log($"Assembly: {relativePath}: {assemblyDefinition.FullName}");

                var references = assemblyDefinition.MainModule.AssemblyReferences;
                foreach (var reference in references)
                {
                    if (assemblyNamesToIgnore.Contains(reference.Name))
                    {
                        continue;
                    }

                    var resolvedAssemblyDefinition = Resolve(reference);
                    if (resolvedAssemblyDefinition == null)
                    {
                        unresolvedAssemblies.Add(reference.Name);
                        diagnostics.Add($"In assembly '{assemblyDefinition.Name.FullName}': Failed to resolve assembly reference to '{reference.FullName}'");

                        continue;
                    }

                    if (IsNetFrameworkAssembly(resolvedAssemblyDefinition))
                    {
                        continue;
                    }

                    CheckAssemblyReference(assemblyDefinition, resolvedAssemblyDefinition, reference);
                }

                CheckMembers(assemblyDefinition);
            }

            CheckAppConfigFiles(appConfigFiles);

            foreach (var ex in diagnostics.OrderBy(s => s))
            {
                Log(ex);
            }

            string reportFile = commandLine.ReportFile;

            if (reportLines.Count > 0)
            {
                if (!File.Exists(reportFile))
                {
                    // initial baseline creation mode
                    File.WriteAllLines(reportFile, reportLines);
                }
                else
                {
                    var baseline = File.ReadAllLines(reportFile);
                    if (!Enumerable.SequenceEqual(baseline, reportLines))
                    {
                        WriteError(@"BinaryCompatChecker failed.
 The current assembly binary compatibility report is different from the checked-in baseline.
 Baseline file: " + reportFile);
                        OutputDiff(baseline, reportLines);
                        try
                        {
                            File.WriteAllLines(reportFile, reportLines);
                        }
                        catch (Exception)
                        {
                        }

                        success = false;
                    }
                }

                ListExaminedAssemblies(reportFile);
            }

            if (commandLine.ReportIVT)
            {
                WriteIVTReport(reportFile);

                WriteIVTReport(
                    reportFile,
                    ".ivt.roslyn.txt",
                    u => IsRoslynAssembly(u.ExposingAssembly) && !IsRoslynAssembly(u.ConsumingAssembly));
            }

            return success;
        }

        public void CheckAssemblyReference(
            AssemblyDefinition referencing,
            AssemblyDefinition referenced,
            AssemblyNameReference reference)
        {
            if (reference.Version != referenced.Name.Version)
            {
                versionMismatches.Add(new VersionMismatch()
                {
                    Referencer = referencing,
                    ExpectedReference = reference,
                    ActualAssembly = referenced
                });
            }

            CheckTypes(referencing, referenced);
        }

        private void CheckMembers(AssemblyDefinition assembly)
        {
            string assemblyFullName = assembly.Name.FullName;
            var module = assembly.MainModule;
            var references = module.GetTypeReferences().Concat(module.GetMemberReferences()).ToList();

            try
            {
                CheckTypeDefinitions(assemblyFullName, module, references);
            }
            catch
            {
            }

            foreach (var memberReference in references)
            {
                try
                {
                    var declaringType = memberReference.DeclaringType ?? (memberReference as TypeReference);
                    if (declaringType != null && declaringType.IsArray)
                    {
                        continue;
                    }

                    IMetadataScope scope = declaringType?.Scope;
                    string referenceToAssembly = scope?.Name;

                    if (referenceToAssembly != null && unresolvedAssemblies.Contains(referenceToAssembly))
                    {
                        // already reported an unresolved assembly; just ignore this one
                        continue;
                    }

                    if (scope is AssemblyNameReference assemblyNameReference)
                    {
                        referenceToAssembly = assemblyNameReference.FullName;
                    }

                    var resolved = memberReference.Resolve();
                    if (resolved == null)
                    {
                        string typeOrMember = memberReference is TypeReference ? "type" : "member";
                        diagnostics.Add($"In assembly '{assemblyFullName}': Failed to resolve {typeOrMember} reference '{memberReference.FullName}' in assembly '{referenceToAssembly}'");
                    }
                    else
                    {
                        var ivtUsage = TryGetIVTUsage(memberReference, resolved);
                        if (ivtUsage != null)
                        {
                            AddIVTUsage(ivtUsage);
                        }
                    }
                }
                catch (AssemblyResolutionException resolutionException)
                {
                    string unresolvedAssemblyName = resolutionException.AssemblyReference?.Name;
                    if (unresolvedAssemblyName == null || unresolvedAssemblies.Add(unresolvedAssemblyName))
                    {
                        diagnostics.Add($"In assembly '{assemblyFullName}': {resolutionException.Message}");
                    }
                }
                catch (Exception ex)
                {
                    bool report = true;

                    TypeReference typeReference = memberReference as TypeReference ??
                        memberReference.DeclaringType;

                    if (typeReference != null && typeReference.Scope.Name is string scope && IsFrameworkName(scope))
                    {
                        report = false;
                    }

                    if (report)
                    {
                        diagnostics.Add($"In assembly '{assemblyFullName}': {ex.Message}");
                    }
                }
            }
        }
    }
}
