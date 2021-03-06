﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Language.Intellisense;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.Library.ObjectBrowser.Lists
{
    internal class ProjectListItem : ObjectListItem
    {
        private readonly string _displayText;

        public ProjectListItem(Project project)
            : base(project.Id, GetProjectGlpyh(project))
        {
            _displayText = project.GetProjectDisplayName();
        }

        private static StandardGlyphGroup GetProjectGlpyh(Project project)
        {
            switch (project.Language)
            {
                case LanguageNames.CSharp:
                    return StandardGlyphGroup.GlyphCoolProject;
                case LanguageNames.VisualBasic:
                    return StandardGlyphGroup.GlyphVBProject;
                default:
                    throw new InvalidOperationException("Unsupported language: " + project.Language);
            }
        }

        public override string DisplayText
        {
            get { return _displayText; }
        }

        public override string FullNameText
        {
            get { return _displayText; }
        }

        public override string SearchText
        {
            get { return _displayText; }
        }
    }
}
