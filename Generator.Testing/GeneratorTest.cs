using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Generator.Testing;

/// <summary>
/// Configuration for generator test compilation settings.
/// </summary>
public class GeneratorTestConfig
{
    private static GeneratorTestConfig? _default;

    /// <summary>
    /// Gets or sets the default configuration used by all tests.
    /// Modify this in your test setup to customize compilation settings globally.
    /// </summary>
    public static GeneratorTestConfig Default
    {
        get => _default ??= new GeneratorTestConfig();
        set => _default = value;
    }

    /// <summary>
    /// Gets or sets the method used to provide reference assemblies for compilation.
    /// Default uses runtime assemblies which work across all frameworks.
    /// </summary>
    public Func<IEnumerable<MetadataReference>>? ReferenceProvider { get; set; }

    /// <summary>
    /// Gets or sets the C# language version for parsing source code.
    /// Default is null (uses Latest).
    /// </summary>
    public LanguageVersion? LangVersion { get; set; }

    /// <summary>
    /// Gets or sets additional compilation options configuration.
    /// </summary>
    public Action<CSharpCompilationOptions>? ConfigureOptions { get; set; }

    internal IEnumerable<MetadataReference> GetReferences()
    {
        return ReferenceProvider?.Invoke() ?? GetDefaultRuntimeReferences();
    }

    internal LanguageVersion GetLanguageVersion()
    {
        return LangVersion ?? LanguageVersion.Latest;
    }

    private static IEnumerable<MetadataReference> GetDefaultRuntimeReferences()
    {
        var assemblies = new List<MetadataReference>();

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedAssemblies)
        {
            var paths = trustedAssemblies.Split(Path.PathSeparator);
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    assemblies.Add(MetadataReference.CreateFromFile(path));
                }
            }

            if (assemblies.Count > 0)
                return assemblies;
        }

        var currentDomainAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var assembly in currentDomainAssemblies)
        {
            if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            {
                assemblies.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        return assemblies;
    }
}

/// <summary>
/// Provides fluent assertion extensions for testing Roslyn incremental generators,
/// including diagnostic validation, output verification, and compilation caching analysis.
/// </summary>
public static class GeneratorTest
{
    /// <summary>
    /// Asserts that a source generator produces a specific diagnostic when processing the provided source code.
    /// </summary>
    /// <typeparam name="TGenerator">The type of the incremental generator to test.</typeparam>
    /// <param name="source">The C# source code to process with the generator.</param>
    /// <param name="diagnosticId">The expected diagnostic ID (e.g., "SG0001", "GN0042").</param>
    /// <param name="severity">The expected severity level of the diagnostic. Defaults to Error.</param>
    /// <param name="additionalSources">Optional additional source files to include in the compilation.</param>
    /// <returns>A task representing the asynchronous assertion operation.</returns>
    /// <exception cref="AssertionException">Thrown when the expected diagnostic is not produced.</exception>
    /// <example>
    /// <code>
    /// await source.ShouldHaveDiagnostic&lt;MyGenerator&gt;("SG0001");
    /// await source.ShouldHaveDiagnostic&lt;MyGenerator&gt;("SG0002", DiagnosticSeverity.Warning);
    /// await source.ShouldHaveDiagnostic&lt;MyGenerator&gt;("SG0003", DiagnosticSeverity.Info);
    /// </code>
    /// </example>
    public static async Task ShouldHaveDiagnostic<TGenerator>(
        this string source,
        string diagnosticId,
        DiagnosticSeverity severity = DiagnosticSeverity.Error,
        params string[] additionalSources)
        where TGenerator : IIncrementalGenerator, new()
    {
        var result = await RunGenerator<TGenerator>(source, additionalSources);
        var diagnostics = result.Diagnostics.Where(d => !d.Id.StartsWith("CS")).ToList();

        if (!diagnostics.Any(d => d.Id == diagnosticId && d.Severity == severity))
        {
            var message = new StringBuilder();
            message.AppendLine($"Expected diagnostic '{diagnosticId}' with severity '{severity}' was not found.");
            message.AppendLine();

            if (diagnostics.Count != 0)
            {
                message.AppendLine("Diagnostics produced:");
                foreach (var d in diagnostics)
                    message.AppendLine($"  {d.Id} ({d.Severity}): {d.GetMessage()}");
            }
            else
            {
                message.AppendLine("No diagnostics were produced by the generator.");
            }

            throw new AssertionException(message.ToString());
        }
    }

    /// <summary>
    /// Asserts that a source generator produces the expected output files when processing the provided source code.
    /// </summary>
    /// <typeparam name="TGenerator">The type of the incremental generator to test.</typeparam>
    /// <param name="source">The C# source code to process with the generator.</param>
    /// <param name="expectedFiles">The hint names of files expected to be generated.</param>
    /// <returns>A task representing the asynchronous assertion operation.</returns>
    /// <exception cref="AssertionException">Thrown when expected files are not generated.</exception>
    /// <example>
    /// <code>
    /// await source.ShouldGenerate&lt;MyGenerator&gt;(
    ///     "MyNamespace.MyClass.g.cs");
    /// </code>
    /// </example>
    public static async Task ShouldGenerate<TGenerator>(
        this string source,
        params string[] expectedFiles)
        where TGenerator : IIncrementalGenerator, new()
    {
        await source.ShouldGenerateFiles<TGenerator>(expectedFiles);
    }

    /// <summary>
    /// Asserts that a source generator properly caches its outputs when run multiple times with identical input.
    /// </summary>
    /// <typeparam name="TGenerator">The type of the incremental generator to test.</typeparam>
    /// <param name="source">The C# source code to process with the generator.</param>
    /// <param name="additionalSources">Optional additional source files to include in the compilation.</param>
    /// <returns>A task representing the asynchronous assertion operation.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the generator produces no output or has no tracked steps.</exception>
    /// <exception cref="AssertionException">Thrown when the generator has caching issues.</exception>
    /// <example>
    /// <code>
    /// await source.ShouldCache&lt;MyGenerator&gt;();
    /// </code>
    /// </example>
    public static async Task ShouldCache<TGenerator>(
        this string source,
        params string[] additionalSources)
        where TGenerator : IIncrementalGenerator, new()
    {
        var (firstRun, secondRun) = await RunGeneratorTwice<TGenerator>(source, additionalSources);

        var realOutput = firstRun.Results[0].GeneratedSources
            .Any(s => !IsInfrastructureFile(s.HintName));

        if (!realOutput)
        {
            var diagnostics = firstRun.Diagnostics
                .Where(d => !d.Id.StartsWith("CS"))
                .ToList();

            var message = new StringBuilder();
            message.AppendLine($"Generator '{typeof(TGenerator).Name}' did not produce any output.");
            message.AppendLine();

            if (diagnostics.Count != 0)
            {
                message.AppendLine("Generator diagnostics:");
                foreach (var d in diagnostics)
                    message.AppendLine($"  {d.Id}: {d.GetMessage()}");
            }
            else
            {
                message.AppendLine("The generator ran but produced no files.");
                message.AppendLine("Verify that your test input matches the generator's requirements.");
            }

            throw new InvalidOperationException(message.ToString());
        }

        var trackedSteps = secondRun.Results[0].TrackedSteps;
        if (!trackedSteps.Any())
        {
            throw new InvalidOperationException(
                $"Generator '{typeof(TGenerator).Name}' has no tracked steps. " +
                "Ensure tracking is enabled in the generator pipeline.");
        }

        var badReasons = new[]
        {
            IncrementalStepRunReason.New,
            IncrementalStepRunReason.Modified,
            IncrementalStepRunReason.Removed
        };

        var problematicSteps = trackedSteps
            .Where(kvp => !IsInfrastructureStep(kvp.Key))
            .Where(kvp => kvp.Value.Any(step => step.Outputs.Any(o => badReasons.Contains(o.Reason))))
            .Select(kvp => kvp.Key)
            .ToList();

        if (problematicSteps.Count != 0)
        {
            var message = new StringBuilder();
            message.AppendLine($"Generator '{typeof(TGenerator).Name}' has caching issues.");
            message.AppendLine("The following steps were not cached on second run:");
            foreach (var step in problematicSteps)
                message.AppendLine($"  {step}");

            throw new AssertionException(message.ToString());
        }
    }

#if DEBUG
    /// <summary>
    /// Generates a detailed diagnostic report of generator caching behavior.
    /// Always throws an exception with the report - intended for debugging only.
    /// Only available in DEBUG builds.
    /// </summary>
    /// <typeparam name="TGenerator">The type of the incremental generator to debug.</typeparam>
    /// <param name="source">The C# source code to process with the generator.</param>
    /// <param name="additionalSources">Optional additional source files to include in the compilation.</param>
    /// <returns>A task representing the asynchronous debug operation.</returns>
    /// <exception cref="Exception">Always throws with detailed caching report.</exception>
    /// <remarks>
    /// This method is intended for debugging purposes only and will always throw an exception with a detailed report.
    /// Use within #if DEBUG blocks in your tests.
    /// </remarks>
    /// <example>
    /// <code>
    /// #if DEBUG
    /// [Fact]
    /// public async Task Debug_CachingBehavior()
    /// {
    ///     await source.DebugCache&lt;MyGenerator&gt;();
    /// }
    /// #endif
    /// </code>
    /// </example>
    public static async Task DebugCache<TGenerator>(
        this string source,
        params string[] additionalSources)
        where TGenerator : IIncrementalGenerator, new()
    {
        var (firstRun, secondRun) = await RunGeneratorTwice<TGenerator>(source, additionalSources);

        var report = new StringBuilder();
        report.AppendLine();
        report.AppendLine($"=== CACHE DEBUG: {typeof(TGenerator).Name} ===");

        var files = firstRun.Results[0].GeneratedSources;
        report.AppendLine();
        report.AppendLine($"Generated Files: {files.Length}");
        foreach (var file in files)
            report.AppendLine($"  - {file.HintName}");

        var realOutput = files.Any(s => !IsInfrastructureFile(s.HintName));
        if (!realOutput)
        {
            report.AppendLine();
            report.AppendLine("[ERROR] NO REAL OUTPUT - only attribute files!");
            report.AppendLine("        Test input doesn't match generator criteria");
        }

        report.AppendLine();
        report.AppendLine("First Run:");
        var firstStepsCount = firstRun.Results.Sum(r => r.TrackedSteps.Count);
        report.AppendLine($"  Total steps: {firstStepsCount}");

        report.AppendLine();
        report.AppendLine("Second Run:");
        var secondSteps = secondRun.Results.SelectMany(r => r.TrackedSteps).ToList();
        report.AppendLine($"  Total steps: {secondSteps.Count}");

        if (secondSteps.Count == 0)
        {
            report.AppendLine("  [WARNING] No tracked steps found!");
            report.AppendLine();
            report.AppendLine("Possible issues:");
            report.AppendLine("  1. Generator didn't find any matching types");
            report.AppendLine("  2. Attribute namespace mismatch");
            report.AppendLine("  3. Pipeline didn't execute");
        }
        else
        {
            var userSteps = secondSteps.Where(s => !IsInfrastructureStep(s.Key)).ToList();
            var infraSteps = secondSteps.Where(s => IsInfrastructureStep(s.Key)).ToList();

            report.AppendLine();
            report.AppendLine($"  User Steps ({userSteps.Count}):");
            var problematicSteps = new List<string>();

            foreach (var kvp in userSteps.OrderBy(x => x.Key))
            {
                var name = kvp.Key;
                var steps = kvp.Value;
                var outputs = steps.SelectMany(s => s.Outputs).ToList();
                var breakdown = GetReasonBreakdown(outputs);

                var goodCount = breakdown.Cached + breakdown.Unchanged;
                var allGood = goodCount == outputs.Count;
                var status = allGood ? "[OK]" : "[FAIL]";

                report.AppendLine($"    {status} {name}: {FormatBreakdown(breakdown)} (of {outputs.Count})");

                if (!allGood)
                {
                    problematicSteps.Add(name);
                }
            }

            report.AppendLine();
            report.AppendLine($"  Infrastructure Steps ({infraSteps.Count}):");
            foreach (var kvp in infraSteps.OrderBy(x => x.Key))
            {
                var name = kvp.Key;
                var steps = kvp.Value;
                var outputs = steps.SelectMany(s => s.Outputs).ToList();
                var breakdown = GetReasonBreakdown(outputs);
                report.AppendLine($"    - {name}: {FormatBreakdown(breakdown)} (of {outputs.Count})");
            }

            report.AppendLine();
            report.AppendLine("  Legend: C=Cached, U=Unchanged, M=Modified, N=New, R=Removed");

            if (problematicSteps.Count == 0)
            {
                report.AppendLine();
                report.AppendLine("[SUCCESS] All user steps properly cached/unchanged!");
            }
            else
            {
                report.AppendLine();
                report.AppendLine($"[FAILED] These user steps have problems: [{string.Join(", ", problematicSteps)}]");
            }
        }

        throw new Exception($"[DEBUG ONLY]{Environment.NewLine}{report}");
    }

    private static (int Cached, int Unchanged, int Modified, int New, int Removed) GetReasonBreakdown(
        List<(object Value, IncrementalStepRunReason Reason)> outputs)
    {
        int cached = 0, unchanged = 0, modified = 0, newCount = 0, removed = 0;

        foreach (var output in outputs)
        {
            switch (output.Reason)
            {
                case IncrementalStepRunReason.Cached:
                    cached++;
                    break;
                case IncrementalStepRunReason.Unchanged:
                    unchanged++;
                    break;
                case IncrementalStepRunReason.Modified:
                    modified++;
                    break;
                case IncrementalStepRunReason.New:
                    newCount++;
                    break;
                case IncrementalStepRunReason.Removed:
                    removed++;
                    break;
            }
        }

        return (cached, unchanged, modified, newCount, removed);
    }

    private static string FormatBreakdown((int Cached, int Unchanged, int Modified, int New, int Removed) breakdown)
    {
        var details = new List<string>(5);
        if (breakdown.Cached > 0) details.Add($"{breakdown.Cached}C");
        if (breakdown.Unchanged > 0) details.Add($"{breakdown.Unchanged}U");
        if (breakdown.Modified > 0) details.Add($"{breakdown.Modified}M");
        if (breakdown.New > 0) details.Add($"{breakdown.New}N");
        if (breakdown.Removed > 0) details.Add($"{breakdown.Removed}R");

        return details.Count > 0 ? string.Join("+", details) : "none";
    }
#endif

    private static async Task ShouldGenerateFiles<TGenerator>(
        this string source,
        string[] expectedFiles,
        params string[] additionalSources)
        where TGenerator : IIncrementalGenerator, new()
    {
        var result = await RunGenerator<TGenerator>(source, additionalSources);

        var actualFiles = result.Results
            .SelectMany(r => r.GeneratedSources)
            .Where(s => !IsInfrastructureFile(s.HintName))
            .Select(s => s.HintName)
            .OrderBy(s => s)
            .ToList();

        var missing = expectedFiles.Except(actualFiles).ToList();

        if (missing.Count != 0)
        {
            var message = new StringBuilder();
            message.AppendLine($"Generator '{typeof(TGenerator).Name}' did not produce expected files.");
            message.AppendLine();
            message.AppendLine("Expected files missing:");
            foreach (var f in missing)
                message.AppendLine($"  {f}");

            message.AppendLine();
            message.AppendLine("Files actually generated:");
            if (actualFiles.Count != 0)
            {
                foreach (var f in actualFiles)
                    message.AppendLine($"  {f}");
            }
            else
            {
                message.AppendLine("  (none)");
            }

            var diagnostics = result.Diagnostics.Where(d => !d.Id.StartsWith("CS")).ToList();
            if (diagnostics.Count != 0)
            {
                message.AppendLine();
                message.AppendLine("Generator diagnostics:");
                foreach (var d in diagnostics)
                    message.AppendLine($"  {d.Id}: {d.GetMessage()}");
            }

            throw new AssertionException(message.ToString());
        }
    }

    private static async Task<GeneratorDriverRunResult> RunGenerator<TGenerator>(
        string source,
        params string[] additionalSources)
        where TGenerator : IIncrementalGenerator, new()
    {
        return await Task.Run(() =>
        {
            var compilation = CreateCompilation(source, additionalSources);
            var generator = new TGenerator().AsSourceGenerator();

            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { generator },
                driverOptions: new GeneratorDriverOptions(
                    IncrementalGeneratorOutputKind.None,
                    trackIncrementalGeneratorSteps: true));

            driver = driver.RunGenerators(compilation);
            return driver.GetRunResult();
        });
    }

    private static async Task<(GeneratorDriverRunResult, GeneratorDriverRunResult)> RunGeneratorTwice<TGenerator>(
        string source,
        params string[] additionalSources)
        where TGenerator : IIncrementalGenerator, new()
    {
        return await Task.Run(() =>
        {
            var compilation = CreateCompilation(source, additionalSources);
            var generator = new TGenerator().AsSourceGenerator();

            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { generator },
                driverOptions: new GeneratorDriverOptions(
                    IncrementalGeneratorOutputKind.None,
                    trackIncrementalGeneratorSteps: true));

            driver = driver.RunGenerators(compilation);
            var first = driver.GetRunResult();

            driver = driver.RunGenerators(compilation.Clone());
            return (first, driver.GetRunResult());
        });
    }

    private static CSharpCompilation CreateCompilation(string source, params string[] additionalSources)
    {
        var config = GeneratorTestConfig.Default;
        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(config.GetLanguageVersion());

        var syntaxTrees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(source, parseOptions)
        };

        foreach (var additional in additionalSources)
        {
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(additional, parseOptions));
        }

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        config.ConfigureOptions?.Invoke(options);

        return CSharpCompilation.Create(
            "TestAssembly",
            syntaxTrees,
            config.GetReferences(),
            options);
    }

    private static bool IsInfrastructureFile(string fileName) =>
        fileName.Contains("EmbeddedAttribute") ||
        fileName.EndsWith("Attribute.g.cs") ||
        fileName.EndsWith("Attribute.cs");

    private static bool IsInfrastructureStep(string name) =>
        name.Contains("Compilation") ||
        name.Contains("_ForAttribute") ||
        name.Contains("ForAttributeWithMetadataName") ||
        name.Contains("ParseOptions") ||
        name.Contains("CompilationOptions") ||
        name.Contains("AdditionalTexts");
}

/// <summary>
/// Represents an assertion failure in generator testing.
/// </summary>
public class AssertionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AssertionException"/> class.
    /// </summary>
    /// <param name="message">The error message describing the assertion failure.</param>
    public AssertionException(string message) : base(message)
    {
    }
}