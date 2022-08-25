// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.VisualStudio.IntegrationTest.Utilities;
using Roslyn.Test.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Roslyn.VisualStudio.IntegrationTests.Workspace
{
    [Trait(Traits.Feature, Traits.Features.Workspace)]
    public class WorkspacesDesktop : WorkspaceBase
    {
        public WorkspacesDesktop()
            : base(WellKnownProjectTemplates.ClassLibrary)
        {
        }

        [IdeFact]
        public override Task OpenCSharpThenVBSolution()
        {
            return base.OpenCSharpThenVBSolution();
        }

        [IdeFact]
        public override Task MetadataReference()
        {
            return base.MetadataReference();
        }

        [IdeFact]
        public override Task ProjectReference()
        {
            return base.ProjectReference();
        }

        [IdeFact]
        public override Task ProjectProperties()
        {
            VisualStudio.SolutionExplorer.CreateSolution(nameof(WorkspacesDesktop));
            var project = new ProjectUtils.Project(ProjectName);
            VisualStudio.SolutionExplorer.AddProject(project, WellKnownProjectTemplates.ClassLibrary, LanguageNames.VisualBasic);
            base.ProjectProperties();
        }
    }
}

