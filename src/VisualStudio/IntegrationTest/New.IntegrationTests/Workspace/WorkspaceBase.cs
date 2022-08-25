// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.VisualStudio.IntegrationTest.Utilities;
using Roslyn.Test.Utilities;
using Roslyn.VisualStudio.IntegrationTests.InProcess;
using Xunit;
using Xunit.Abstractions;
using ProjectUtils = Microsoft.VisualStudio.IntegrationTest.Utilities.Common.ProjectUtils;

#pragma warning disable xUnit1013 // currently there are public virtual methods that are overridden by derived types

namespace Roslyn.VisualStudio.IntegrationTests.Workspace
{
    public abstract class WorkspaceBase : AbstractEditorTest
    {
        private readonly string _defaultProjectTemplate;

        protected WorkspaceBase(string projectTemplate) : base(nameof(WorkspaceBase), projectTemplate)
        {
            _defaultProjectTemplate = projectTemplate;
        }

        protected override string LanguageName => LanguageNames.CSharp;

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync().ConfigureAwait(true);
            await TestServices.Workspace.SetFullSolutionAnalysisAsync(true, HangMitigatingCancellationToken);
        }

        public virtual async Task OpenCSharpThenVBSolution()
        {
            await TestServices.Editor.SetTextAsync(@"using System; class Program { Exception e; }", HangMitigatingCancellationToken);
            await TestServices.Editor.PlaceCaretAsync("Exception", charsOffset: 0, HangMitigatingCancellationToken);
            await TestServices.EditorVerifier.CurrentTokenTypeAsync(tokenType: "class name", HangMitigatingCancellationToken);
            await TestServices.SolutionExplorer.CloseSolutionAsync(HangMitigatingCancellationToken);
            await TestServices.SolutionExplorer.CreateSolutionAsync(nameof(WorkspaceBase), HangMitigatingCancellationToken);
            await TestServices.SolutionExplorer.AddProjectAsync("TestProj", WellKnownProjectTemplates.ClassLibrary, languageName: LanguageNames.VisualBasic, HangMitigatingCancellationToken);
            await TestServices.SolutionExplorer.RestoreNuGetPackagesAsync(HangMitigatingCancellationToken);
            await TestServices.Editor.SetTextAsync(@"Imports System
Class Program
    Private e As Exception
End Class", HangMitigatingCancellationToken);
            await TestServices.Editor.PlaceCaretAsync("Exception", charsOffset: 0, HangMitigatingCancellationToken);
            await TestServices.EditorVerifier.CurrentTokenTypeAsync(tokenType: "class name", HangMitigatingCancellationToken);
        }

        public virtual async Task MetadataReference()
        {
            //var windowsBase = new ProjectUtils.AssemblyReference("WindowsBase");
            //var project = new ProjectUtils.Project(ProjectName);

            await TestServices.SolutionExplorer.AddDllReferenceAsync("TestProj", Path.Combine("WindowsBase", "WindowsBase.csproj"), HangMitigatingCancellationToken);
            await TestServices.Workspace.WaitForAllAsyncOperationsAsync(new[] { FeatureAttribute.Workspace }, HangMitigatingCancellationToken);

            await TestServices.Editor.SetTextAsync("class C { System.Windows.Point p; }", HangMitigatingCancellationToken);
            await TestServices.Editor.PlaceCaretAsync("Point", charsOffset: 0, HangMitigatingCancellationToken);
            await TestServices.EditorVerifier.CurrentTokenTypeAsync("struct name", HangMitigatingCancellationToken);

            await TestServices.SolutionExplorer.RemoveDllReferenceAsync("TestProj", Path.Combine("WindowsBase", "WindowsBase.csproj"), HangMitigatingCancellationToken);
            await TestServices.Workspace.WaitForAllAsyncOperationsAsync(new[] { FeatureAttribute.Workspace }, HangMitigatingCancellationToken);

            await TestServices.EditorVerifier.CurrentTokenTypeAsync("identifier", HangMitigatingCancellationToken);

            //VisualStudio.SolutionExplorer.AddMetadataReference(windowsBase, project);
            //VisualStudio.Editor.SetText("class C { System.Windows.Point p; }");
            //VisualStudio.Editor.PlaceCaret("Point");
            //VisualStudio.Editor.Verify.CurrentTokenType("struct name");
            //VisualStudio.SolutionExplorer.RemoveMetadataReference(windowsBase, project);
            //VisualStudio.Editor.Verify.CurrentTokenType("identifier");
        }

        public virtual async Task ProjectReference()
        {
            //var project = new ProjectUtils.Project(ProjectName);
            //var csProj2 = new ProjectUtils.Project("CSProj2");

            var csProj2 = "CSProj2";
            var project = "TestProj";
            await TestServices.SolutionExplorer.AddProjectAsync(csProj2, projectTemplate: _defaultProjectTemplate, languageName: LanguageName, HangMitigatingCancellationToken);

            var projectName = new ProjectUtils.ProjectReference(ProjectName);
            await TestServices.SolutionExplorer.AddProjectReferenceAsync(projectName: csProj2, project, HangMitigatingCancellationToken);
            await TestServices.SolutionExplorer.RestoreNuGetPackagesAsync(HangMitigatingCancellationToken);

            await TestServices.SolutionExplorer.AddFileAsync(project, "Program.cs", open: true, contents: "public class Class1 { }", cancellationToken: HangMitigatingCancellationToken);
            await TestServices.SolutionExplorer.AddFileAsync(csProj2, "Program.cs", open: true, contents: "public class Class2 { Class1 c; }", cancellationToken: HangMitigatingCancellationToken);
            await TestServices.SolutionExplorer.OpenFileAsync(csProj2, "Program.cs", HangMitigatingCancellationToken);

            await TestServices.Editor.PlaceCaretAsync("Class1", charsOffset: 0, HangMitigatingCancellationToken);
            await TestServices.EditorVerifier.CurrentTokenTypeAsync("class name", HangMitigatingCancellationToken);
            VisualStudio.SolutionExplorer.RemoveProjectReference(projectReferenceName: projectName, projectName: csProj2);
            VisualStudio.Editor.Verify.CurrentTokenType("identifier");
        }

        public virtual void ProjectProperties()
        {
            VisualStudio.Editor.SetText(@"Module Program
    Sub Main()
        Dim x = 42
        M(x)
    End Sub
    Sub M(p As Integer)
    End Sub
    Sub M(p As Object)
    End Sub
End Module");
            VisualStudio.Editor.PlaceCaret("(x)", charsOffset: -1);
            var project = new ProjectUtils.Project(ProjectName);
            VisualStudio.Workspace.SetOptionInfer(project.Name, true);
            VisualStudio.Editor.InvokeQuickInfo();
            Assert.Equal("Sub Program.M(p As Integer) (+ 1 overload)", VisualStudio.Editor.GetQuickInfo());
            VisualStudio.Workspace.SetOptionInfer(project.Name, false);
            VisualStudio.Editor.InvokeQuickInfo();
            Assert.Equal("Sub Program.M(p As Object) (+ 1 overload)", VisualStudio.Editor.GetQuickInfo());
        }

        [WpfFact]
        public void RenamingOpenFiles()
        {
            var project = new ProjectUtils.Project(ProjectName);
            VisualStudio.SolutionExplorer.AddFile(project, "BeforeRename.cs", open: true);

            // Verify we are connected to the project before...
            Assert.Contains(ProjectName, VisualStudio.Editor.GetProjectNavBarItems());

            VisualStudio.SolutionExplorer.RenameFile(project, "BeforeRename.cs", "AfterRename.cs");

            // ...and after.
            Assert.Contains(ProjectName, VisualStudio.Editor.GetProjectNavBarItems());
        }

        [WpfFact]
        public virtual void RenamingOpenFilesViaDTE()
        {
            var project = new ProjectUtils.Project(ProjectName);
            VisualStudio.SolutionExplorer.AddFile(project, "BeforeRename.cs", open: true);

            // Verify we are connected to the project before...
            Assert.Contains(ProjectName, VisualStudio.Editor.GetProjectNavBarItems());

            VisualStudio.SolutionExplorer.RenameFileViaDTE(project, "BeforeRename.cs", "AfterRename.cs");

            // ...and after.
            Assert.Contains(ProjectName, VisualStudio.Editor.GetProjectNavBarItems());
        }
    }
}

