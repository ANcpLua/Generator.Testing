# Generator.Testing

Fluent testing for Roslyn incremental generators.

## Install

```xml
<PackageReference Include="Generator.Testing" Version="1.0.0"/>
```

## Quick Start

```csharp
[Fact]
public async Task Generator_ProducesExpectedOutput()
{
    await """
        [GenerateToString]
        public partial class Person 
        { 
            public string Name { get; set; }
        }
        """.ShouldGenerate<MyGenerator>("Person.g.cs");
}
```

## Features

### Test Diagnostics

```csharp
await source.ShouldHaveDiagnostic<MyGenerator>("GE0001", DiagnosticSeverity.Info);
await source.ShouldHaveDiagnostic<MyGenerator>("GE0002", DiagnosticSeverity.Warning);
await source.ShouldHaveDiagnostic<MyGenerator>("GE0003"); // Default: Error
```

### Test Caching Performance

```csharp
await source.ShouldCache<MyGenerator>();
```

### Test Generated Files

```csharp
await source.ShouldGenerate<MyGenerator>("Person.g.cs");
```

### Debug Caching (DEBUG builds only)

```csharp
#if DEBUG
await source.DebugCache<MyGenerator>(); // Throws with detailed caching report
#endif
```

The framework automatically filters out infrastructure files like attribute definitions (`*Attribute.g.cs`) and embedded
markers.

Cause of `AddEmbeddedAttributeDefinition`

## Configuration

Force a specific C# version when needed:

```csharp
public MyGeneratorTests()
{
    GeneratorTestConfig.Default = new() { LangVersion = LanguageVersion.CSharp13 };
}
```

## Compatibility

Works with all major test frameworks:

- xUnit: `[Fact]`
- NUnit: `[Test]`
- MSTest: `[TestMethod]`

## Requirements

- .NET Standard 2.0+
- Roslyn 4.14.0 (`IIncrementalGenerator`)

## License

This project is licensed under the [MIT License](LICENSE).
