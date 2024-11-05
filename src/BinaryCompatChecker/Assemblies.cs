﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;

namespace BinaryCompatChecker;

public partial class Checker
{
    IAssemblyResolver resolver;

    private static Dictionary<string, bool> frameworkAssemblyNames = new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> frameworkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib",
        "Accessibility",
        "Microsoft.CSharp",
        "Microsoft.VisualBasic",
        "Microsoft.VisualC",
        "Microsoft.WindowsCE.Forms",
        "netstandard",
        "PresentationCore",
        "PresentationFramework",
        "ReachFramework",
        "System",
        "UIAutomationClient",
        "UIAutomationProvider",
        "UIAutomationTypes",
        "WindowsBase",
        "WindowsFormsIntegration"
    };

    private string dotnetRuntimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);

    private static string desktopNetFrameworkDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET");

    private List<string> desktopNetFrameworkDirectories = new List<string>
    {
        Path.Combine(desktopNetFrameworkDirectory, "assembly", "GAC_MSIL"),
        Path.Combine(desktopNetFrameworkDirectory, "assembly", "GAC_32"),
        Path.Combine(desktopNetFrameworkDirectory, "assembly", "GAC_64"),
    };

    private static string mscorlibFilePath = Path.Combine(desktopNetFrameworkDirectory, "Framework", "v4.0.30319", "mscorlib.dll");

    private class VersionMismatch
    {
        public AssemblyDefinition Referencer;
        public AssemblyNameReference ExpectedReference;
        public AssemblyDefinition ActualAssembly;

        public List<string> HandledByAppConfigs { get; } = new();
    }

    public class IVTUsage
    {
        public string ExposingAssembly { get; set; }
        public string ConsumingAssembly { get; set; }
        public string Member { get; set; }
    }

    private readonly List<VersionMismatch> versionMismatches = new List<VersionMismatch>();

    private readonly HashSet<string> resolvedFromFramework = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public class CustomAssemblyResolver : BaseAssemblyResolver
    {
        private readonly Checker checker;

        public CustomAssemblyResolver(Checker checker)
        {
            this.checker = checker;
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack(); // see https://github.com/KirillOsenkov/MetadataTools/issues/4
            var resolved = checker.Resolve(name);
            resolved = resolved ?? base.Resolve(name, parameters);
            return resolved;
        }
    }

    private static bool IsFrameworkName(string shortName)
    {
        return
            shortName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
            frameworkNames.Contains(shortName);
    }

    private static bool IsRoslynAssembly(string assemblyName)
    {
        if (assemblyName.Contains("Microsoft.CodeAnalysis") || assemblyName.Contains("VisualStudio.LanguageServices"))
        {
            return true;
        }

        return false;
    }

    private static bool IsNetFrameworkAssembly(string assemblyName)
    {
        frameworkAssemblyNames.TryGetValue(assemblyName, out bool result);
        return result;
    }

    /// <summary>
    /// Returns true if the <paramref name="assembly"/> is .NET Framework assembly.
    /// </summary>
    private static bool IsNetFrameworkAssembly(AssemblyDefinition assembly)
    {
        string key = assembly.MainModule.FileName;
        if (frameworkAssemblyNames.TryGetValue(key, out bool result))
        {
            return result;
        }

        // Hacky way of detecting it.
        result = assembly
            .CustomAttributes
            .FirstOrDefault(a => IsAssemblyProductFramework(a) || IsAssemblyMetadataFramework(a)) != null;
        frameworkAssemblyNames[key] = result;
        return result;
    }

    private static bool IsAssemblyMetadataFramework(CustomAttribute a)
    {
        return
            a.AttributeType.Name == "AssemblyMetadataAttribute" &&
            a.ConstructorArguments != null &&
            a.ConstructorArguments.Count > 0 &&
            a.ConstructorArguments[0].Value.ToString() == ".NETFrameworkAssembly";
    }

    private static bool IsAssemblyProductFramework(CustomAttribute a)
    {
        return
            a.AttributeType.Name == "AssemblyProductAttribute" &&
            a.ConstructorArguments != null &&
            a.ConstructorArguments.FirstOrDefault(c =>
                c.Value.ToString() == "Microsoft® .NET Framework" ||
                c.Value.ToString() == "Microsoft® .NET").Value != null;
    }

    /// <summary>
    /// Returns true if the <paramref name="assembly"/> is a facade assembly with type forwarders only.
    /// </summary>
    private static bool IsFacadeAssembly(AssemblyDefinition assembly)
    {
        var types = assembly.MainModule.Types;
        if (types.Count == 1 && types[0].FullName == "<Module>" && assembly.MainModule.HasExportedTypes)
        {
            return true;
        }

        return false;
    }

    private void OnAssemblyResolved(AssemblyDefinition assemblyDefinition)
    {
        //string filePath = assemblyDefinition.MainModule.FileName;
        //WriteLine(filePath, ConsoleColor.DarkGray);
    }

    private void OnAssemblyLoaded(AssemblyDefinition assemblyDefinition)
    {
        string filePath = assemblyDefinition.MainModule.FileName;
        if (!files.Contains(filePath))
        {
            WriteLine(filePath, ConsoleColor.DarkGray);
        }
    }

    private AssemblyDefinition Resolve(AssemblyNameReference reference)
    {
        if (resolveCache.TryGetValue(reference.FullName, out AssemblyDefinition result))
        {
            return result;
        }

        string filePath = TryResolve(reference);

        if (File.Exists(filePath))
        {
            result = Load(filePath);
            resolveCache[reference.FullName] = result;

            if (result != null)
            {
                OnAssemblyResolved(result);
            }
        }
        else
        {
            resolveCache[reference.FullName] = null;
        }

        return result;
    }

    private string TryResolve(AssemblyNameReference reference)
    {
        string result;

        // first try to see if we already have the exact version loaded
        result = TryResolveFromLoadedAssemblies(reference, strictVersion: true);
        if (result != null)
        {
            return result;
        }

        result = TryResolveFromInputFiles(reference);
        if (result != null)
        {
            return result;
        }

        result = TryResolveFromFramework(reference);
        if (result != null)
        {
            return result;
        }

        result = TryResolveFromCustomDirectories(reference);
        if (result != null)
        {
            return result;
        }

        // try any version as the last resort
        result = TryResolveFromLoadedAssemblies(reference, strictVersion: false);
        if (result != null)
        {
            return result;
        }

        return null;
    }

    private string TryResolveFromLoadedAssemblies(AssemblyNameReference reference, bool strictVersion = true)
    {
        foreach (var assemblyDefinition in filePathToModuleDefinition)
        {
            if (assemblyDefinition.Value == null)
            {
                continue;
            }

            if (assemblyDefinition.Value.Name.FullName == reference.FullName ||
                (!strictVersion && string.Equals(Path.GetFileNameWithoutExtension(assemblyDefinition.Key), reference.Name, StringComparison.OrdinalIgnoreCase)))
            {
                string filePath = assemblyDefinition.Value.MainModule.FileName;
                return filePath;
            }
        }

        return null;
    }

    private string TryResolveFromInputFiles(AssemblyNameReference reference)
    {
        foreach (var file in commandLine.Files)
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(file), reference.Name, StringComparison.OrdinalIgnoreCase))
            {
                var assemblyDefinition = Load(file);
                if (assemblyDefinition != null && !IsFacadeAssembly(assemblyDefinition))
                {
                    return file;
                }
            }
        }

        foreach (var directory in commandLine.AllDirectories)
        {
            string candidate = Path.Combine(directory, reference.Name + ".dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string TryResolveFromFramework(AssemblyNameReference reference)
    {
        string shortName = reference.Name;
        Version version = reference.Version;

        bool isFrameworkName = IsFrameworkName(shortName);
        if (!isFrameworkName)
        {
            return null;
        }

        bool desktop = false;
        bool isFrameworkRedirect = false;

        if (IsWindows)
        {
            // 4.0.1.0 is .NETPortable,Version=v5.0, still resolve from desktop
            if (version <= new Version(4, 0, 10, 0))
            {
                desktop = true;
            }

            if (frameworkRedirects.ContainsKey(reference.Name))
            {
                desktop = true;
                isFrameworkRedirect = true;
            }
        }

        // resolve desktop framework assemblies from the GAC
        if (desktop)
        {
            if (shortName == "mscorlib" && File.Exists(mscorlibFilePath))
            {
                resolvedFromFramework.Add(mscorlibFilePath);
                return mscorlibFilePath;
            }

            foreach (var dir in desktopNetFrameworkDirectories)
            {
                var combined = Path.Combine(dir, shortName);
                if (Directory.Exists(combined))
                {
                    var first = Directory.GetDirectories(combined);
                    foreach (var item in first)
                    {
                        try
                        {
                            var candidate = Path.Combine(item, shortName + ".dll");
                            if (File.Exists(candidate))
                            {
                                var fileVersion = AssemblyName.GetAssemblyName(candidate);
                                if (fileVersion != null)
                                {
                                    bool match = false;

                                    if (fileVersion.Name.Equals(reference.Name, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (string.Equals(fileVersion.FullName, reference.FullName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            match = true;
                                        }
                                        else if (
                                            reference.Version.Major == 0 &&
                                            reference.Version.Minor == 0 &&
                                            reference.Version.Build == 0 &&
                                            reference.Version.Revision == 0)
                                        {
                                            match = true;
                                        }
                                        else if (isFrameworkRedirect && frameworkRedirects.TryGetValue(reference.Name, out var redirectVersion))
                                        {
                                            if (reference.Version <= redirectVersion)
                                            {
                                                match = true;
                                            }
                                        }
                                    }

                                    if (match)
                                    {
                                        resolvedFromFramework.Add(candidate);
                                        return candidate;
                                    }
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
        else
        {
            var parent = Path.GetDirectoryName(dotnetRuntimeDirectory);

            string versionPrefix = version.Major.ToString();

            // .NETCore 3 has versions like 4.1.1.0 or 4.2.2.0, not confusingly
            if (version.Major == 4 &&
                (version.Minor == 1 || version.Minor == 2))
            {
                versionPrefix = "3";
            }

            var versionDirectories = Directory.GetDirectories(parent, versionPrefix + "*");
            if (versionDirectories.Length > 0)
            {
                var lastVersion = versionDirectories[versionDirectories.Length - 1];
                string versionCandidate = Path.Combine(lastVersion, shortName + ".dll");
                if (File.Exists(versionCandidate))
                {
                    resolvedFromFramework.Add(versionCandidate);
                    return versionCandidate;
                }
            }

            string frameworkCandidate = Path.Combine(dotnetRuntimeDirectory, shortName + ".dll");
            if (File.Exists(frameworkCandidate))
            {
                resolvedFromFramework.Add(frameworkCandidate);
                return frameworkCandidate;
            }
        }

        return null;
    }

    private string TryResolveFromCustomDirectories(AssemblyNameReference reference)
    {
        string shortName = reference.Name;

        foreach (var customResolveDirectory in commandLine.CustomResolveDirectories)
        {
            var candidate = Path.Combine(customResolveDirectory, shortName + ".dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private AssemblyDefinition Load(string filePath)
    {
        if (!filePathToModuleDefinition.TryGetValue(filePath, out var assemblyDefinition))
        {
            try
            {
                if (!GuiLabs.Metadata.PEFile.IsManagedAssembly(filePath))
                {
                    filePathToModuleDefinition[filePath] = null;
                    return null;
                }

                var readerParameters = new ReaderParameters
                {
                    AssemblyResolver = this.resolver,
                    InMemory = true
                };
                assemblyDefinition = AssemblyDefinition.ReadAssembly(filePath, readerParameters);
                filePathToModuleDefinition[filePath] = assemblyDefinition;

                OnAssemblyLoaded(assemblyDefinition);

                if (!IsNetFrameworkAssembly(assemblyDefinition))
                {
                    string relativePath = GetRelativePath(filePath);
                    string targetFramework = GetTargetFramework(assemblyDefinition);
                    if (targetFramework != null)
                    {
                        targetFramework = " " + targetFramework;
                    }

                    assembliesExamined.Add($"{relativePath}    {assemblyDefinition.Name.Version}{targetFramework}");
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(ex.ToString());
                return null;
            }
        }

        return assemblyDefinition;
    }

    private string GetRelativePath(string filePath)
    {
        if (filePath.StartsWith(Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            filePath = filePath.Substring(Environment.CurrentDirectory.Length + 1);
        }

        return filePath;
    }

    private static Dictionary<string, Version> frameworkRedirects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.VisualBasic"] = new Version("7.0.5000.0"),
        ["Microsoft.VisualBasic"] = new Version("7.0.5500.0"),
        ["Microsoft.WindowsCE.Forms"] = new Version("1.0.5000.0"),
        ["Microsoft.WindowsCE.Forms"] = new Version("1.0.5500.0"),
        ["System"] = new Version("1.0.0.0"),
        ["System"] = new Version("1.0.5000.0"),
        ["System"] = new Version("1.0.5500.0"),
        ["System.AppContext"] = new Version("4.1.0.0"),
        ["System.ComponentModel.Primitives"] = new Version("4.1.0.0"),
        ["System.ComponentModel.TypeConverter"] = new Version("4.1.0.0"),
        ["System.Data"] = new Version("1.0.5000.0"),
        ["System.Data"] = new Version("1.0.5500.0"),
        ["System.Data.Common"] = new Version("4.1.0.0"),
        ["System.Data.Common"] = new Version("4.2.0.0"),
        ["System.Diagnostics.Process"] = new Version("4.1.0.0"),
        ["System.Diagnostics.StackTrace"] = new Version("4.1.0.0"),
        ["System.Diagnostics.Tracing"] = new Version("4.1.0.0"),
        ["System.Diagnostics.Tracing"] = new Version("4.2.0.0"),
        ["System.Drawing"] = new Version("1.0.5000.0"),
        ["System.Drawing"] = new Version("1.0.5500.0"),
        ["System.Globalization.Extensions"] = new Version("4.1.0.0"),
        ["System.IO"] = new Version("4.1.0.0"),
        ["System.IO.Compression"] = new Version("4.2.0.0"),
        ["System.Linq"] = new Version("4.1.0.0"),
        ["System.Linq.Expressions"] = new Version("4.1.0.0"),
        ["System.Net.Http"] = new Version("4.2.0.0"),
        ["System.Net.NetworkInformation"] = new Version("4.1.0.0"),
        ["System.Net.Sockets"] = new Version("4.1.0.0"),
        ["System.Net.Sockets"] = new Version("4.2.0.0"),
        ["System.Reflection"] = new Version("4.1.0.0"),
        ["System.Runtime"] = new Version("4.1.0.0"),
        ["System.Runtime.Extensions"] = new Version("4.1.0.0"),
        ["System.Runtime.InteropServices"] = new Version("4.1.0.0"),
        ["System.Runtime.Serialization.Primitives"] = new Version("4.1.0.0"),
        ["System.Runtime.Serialization.Primitives"] = new Version("4.2.0.0"),
        ["System.Runtime.Serialization.Xml"] = new Version("4.1.0.0"),
        ["System.Security.Cryptography.Algorithms"] = new Version("4.2.0.0"),
        ["System.Security.Cryptography.Algorithms"] = new Version("4.3.0.0"),
        ["System.Security.Cryptography.X509Certificates"] = new Version("4.1.0.0"),
        ["System.Security.SecureString"] = new Version("4.1.0.0"),
        ["System.Text.RegularExpressions"] = new Version("4.1.0.0"),
        ["System.Threading.Overlapped"] = new Version("4.1.0.0"),
        ["System.Web.Services"] = new Version("1.0.5000.0"),
        ["System.Web.Services"] = new Version("1.0.5500.0"),
        ["System.Windows.Forms"] = new Version("1.0.5000.0"),
        ["System.Windows.Forms"] = new Version("1.0.5500.0"),
        ["System.Xml"] = new Version("1.0.0.0"),
        ["System.Xml"] = new Version("1.0.5000.0"),
        ["System.Xml"] = new Version("1.0.5500.0"),
        ["System.Xml.ReaderWriter"] = new Version("4.1.0.0"),
        ["System.Xml.XPath.XDocument"] = new Version("4.1.0.0"),
    };
}