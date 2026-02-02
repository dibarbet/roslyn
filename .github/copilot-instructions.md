** CRITICAL - ALWAYS USE LSP FOR CODE NAVIGATION **

This repository is extremely large. **You MUST use LSP tools for any code navigation task.** Never use grep or text search when an LSP tool can accomplish the same goal.

**LSP tools you MUST use (in order of preference):**
- **Go to Definition** - Find where any symbol is defined (classes, methods, properties, fields, operators like `==`, etc.)
- **Find All References / List Usages** - Find all usages of a symbol across the codebase
- **Go to Implementation** - Find implementations of interfaces or abstract members

**When to use LSP (ALWAYS prefer these over grep/text search):**
- Finding where a class, method, or type is defined → **Use "Go to Definition"**
- Finding all places a method is called → **Use "Find All References" / "List Usages"**
- Finding implementations of an interface → **Use "Go to Implementation"**
- Understanding what a symbol refers to → **Use "Go to Definition"**
- Navigating to base types or overridden members → **Use LSP navigation**
- Finding where a variable is assigned or read → **Use "Find All References"**

**Only use grep/text search as a LAST RESORT when:**
- Searching for string literals, comments, or non-code text
- LSP tools are unavailable or explicitly fail after trying
- Searching for patterns that aren't navigable symbols (e.g., TODO comments, magic strings)

**Why this is mandatory:** LSP tools are semantic and understand the code structure. Grep is just text matching and will give incomplete, incorrect, or overwhelming results in a codebase this size. Using grep when LSP works wastes time and produces inferior results.

# Roslyn (.NET Compiler Platform) AI Coding Instructions

## Architecture Overview

**Core Components** (layered from bottom-up):
- **Compilers** (`src/Compilers/`): C# and VB.NET compilers with syntax trees, semantic models, symbols, and emit APIs
- **Workspaces** (`src/Workspaces/`): Solution/project model, document management, and host services
- **Features** (`src/Features/`): Language-agnostic IDE features (refactoring, completion, diagnostics)
- **EditorFeatures** (`src/EditorFeatures/`): Editor-specific implementations and text buffer integration
- **VisualStudio** (`src/VisualStudio/`): VS-specific language services and UI integration

## Development Workflow

**Building**:
- `build.sh` - Full solution build
- `dotnet build Compilers.slnf` - Compiler-only build  
- `dotnet msbuild <path to csproj> /t:UpdateXlf` - Update .xlf files when their corresponding .resx file is modified

**Testing**:
- `test.sh` - Run all tests
- `dotnet test` for specific test projects
- Tests inherit from base classes like `AbstractLanguageServerProtocolTests`, `WorkspaceTestBase`
- Use `[UseExportProvider]` for MEF-dependent tests

**Formatting**:
- Whitespace formatting preferences are stored in the `.editorconfig` file
- When running `dotnet format whitespace` use the `--folder .` option followed by `--include <path to file>` to avoid a design-time build
- Apply formatting preferences to any modified .cs or .vb file
- **Important**: Blank lines must not contain any whitespace characters (spaces or tabs). This will cause linting errors that must be fixed.

## Code Patterns

**Service Architecture** (use MEF consistently):
```csharp
[ExportLanguageService(typeof(IMyService), LanguageNames.CSharp), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class CSharpMyService : IMyService
```

**Roslyn API Usage**:
```csharp
// Always use immutable patterns
var newTree = oldTree.WithChangedText(newText);
var newDocument = oldDocument.WithSyntaxTree(newTree);

// Semantic analysis
var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
var symbolInfo = semanticModel.GetSymbolInfo(expression);
```

**Testing Conventions**:
- Inherit from `TestBase` or language-specific base classes
- Use `UseExportProvider` for MEF services
- Test utilities in `Microsoft.CodeAnalysis.Test.Utilities`
- Language-specific test bases: `CSharpTestBase`, `VisualBasicTestBase`
- Add `[WorkItem("https://github.com/dotnet/roslyn/issues/issueNumber")]` attribute to tests that fix specific GitHub issues
- Prefer raw string literals (`"""..."""`) over verbatim strings (`@"..."`) when creating test source code
- Avoid unnecessary intermediary assertions - tests should do the minimal amount of work to validate just the core issue being addressed
  - In tests, use concise methods like `.Single()` instead of asserting count and extracting elements
  - For compiler tests, validate diagnostics (e.g., `comp.VerifyEmitDiagnostics()`) so reviewers can easily see if the code is in error or represents something legal

## Critical Integration Points

- **Language Server Protocol**: `src/LanguageServer/` contains LSP implementation used by VS Code extension
- **ServiceHub**: Remote services (`src/Workspaces/Remote/`) run out-of-process for performance
- **Analyzers**: `src/Analyzers/` for static analysis, separate from `src/RoslynAnalyzers/` (internal tooling)
- **VSIX Packaging**: Multiple deployment targets - `src/VisualStudio/Setup/` for main VS integration

## Key Conventions

- **Namespace Strategy**: `Microsoft.CodeAnalysis.[Language].[Area]` (e.g., `Microsoft.CodeAnalysis.CSharp.Formatting`)
- **File Organization**: Group by feature area, separate language-specific implementations
- **Immutability**: All syntax trees, documents, and solutions are immutable - create new instances for changes
- **Cancellation**: Always thread `CancellationToken` through async operations
- **MEF Lifecycle**: Use `[ImportingConstructor]` with obsolete attribute for MEF v2 compatibility
- **PROTOTYPE Comments**: Only used to track follow-up work in feature branches and are disallowed in main branch
- **Code Formatting**: Avoid trailing spaces and blank lines (lines with only whitespace). Ensure all lines either have content or are completely empty.

## Common Gotchas

- Follow existing conventions in the file
- Language services must be exported per-language, not shared across C#/VB
- Test failures often indicate MEF composition issues - check export attributes
- VSIX deployment targets multiple architectures - ensure platform-specific assets are handled
- ServiceHub components require special deployment considerations for .NET Core vs Framework

## Essential Files for Context

- `docs/wiki/Roslyn-Overview.md` - Architecture deep-dive
- `docs/contributing/Building, Debugging, and Testing on Unix.md` - Development setup
- `src/Compilers/Core/Portable/` - Core compiler APIs
- `src/Workspaces/Core/Portable/` - Workspace object model
- Solution filters: `Roslyn.slnx`, `Compilers.slnf`, `Ide.slnf` for focused builds
