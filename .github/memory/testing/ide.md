---
coverage: IDE-layer (src/{Analyzers,CodeStyle,Features,Workspaces,EditorFeatures,VisualStudio,LanguageServer}) test base classes & authoring conventions
---

# IDE — Testing

Layer-specific test guidance for the IDE/Workspaces stack under
`src/{Features,Analyzers,EditorFeatures,...}`.

## Test workspace (MEF-dependent tests)

```csharp
[UseExportProvider]
public class MyTests
{
    [Fact]
    public async Task TestSomething()
    {
        var workspace = EditorTestWorkspace.CreateCSharp("class C { }");
        var document = workspace.Documents.Single();
    }
}
```

## Conventions

- Use `[UseExportProvider]` for any test that depends on MEF services (a missing
  attribute typically surfaces as an unrelated-looking failure).
- Analyzer tests inherit from
  `AbstractCSharpDiagnosticProviderBasedUserDiagnosticTest_NoEditor` (and the VB
  equivalents).
- For analyzer/code-fix tests, use `TestInRegularAndScriptAsync` /
  `TestMissingInRegularAndScriptAsync`.
- Prefer raw string literals (`"""..."""`) over verbatim strings (`@"..."`) for
  test source code.
- Keep tests focused — avoid unnecessary intermediary assertions; use `.Single()`
  rather than asserting a count then indexing.

## Language server file watchers

`Microsoft.CodeAnalysis.LanguageServer.UnitTests` contains native filesystem coverage in
`DefaultFileChangeWatcherTests`, deterministic shared-tree/factory scenarios in
`AggregatingFileChangeWatcherTests`, and protocol registration/notification coverage in
`LspFileChangeWatcherTests` and `LspDirectoryWatcherFactoryTests`.
Use recording factories and gated RPC acknowledgements for replacement/lifetime races;
await the workspace asynchronous-operation listener rather than sleeping.
LSP notifications are asynchronous: wait for handler completion before asserting delivery,
and account for URI path normalization (including Windows drive-letter casing).
