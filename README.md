# Generator.Testing

Fluent testing for Roslyn incremental generators.
[example usage](https://github.com/ANcpLua/MapsterExtensions.Generator/blob/main/MapsterExtensions.Generator.Tests/MapsterExtension_Functional_Tests.cs) 

## How to:

### Caching 

```csharp
await source.ShouldCache<MyGenerator>();
```

### Generated Files

```csharp
await source.ShouldGenerate<MyGenerator>("Person.g.cs");
```

### Test Diagnostics

```csharp
await source.ShouldHaveDiagnostic<MyGenerator>("GE0001", DiagnosticSeverity.Info);
await source.ShouldHaveDiagnostic<MyGenerator>("GE0002", DiagnosticSeverity.Warning);
await source.ShouldHaveDiagnostic<MyGenerator>("GE0003"); // Default: Error
```

### Debug (DEBUG builds only)

```csharp
#if DEBUG
await source.DebugCache<MyGenerator>(); // Throws with detailed caching report
#endif
```
