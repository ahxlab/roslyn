// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.ErrorReporting;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.EditAndContinue
{
    internal sealed class EditSession
    {
        [SuppressMessage("Performance", "RS0008", Justification = "Equality not actually implemented")]
        private struct Analysis
        {
            public readonly Document Document;
            public readonly AsyncLazy<DocumentAnalysisResults> Results;

            public Analysis(Document document, AsyncLazy<DocumentAnalysisResults> results)
            {
                this.Document = document;
                this.Results = results;
            }

            public override bool Equals(object obj)
            {
                throw ExceptionUtilities.Unreachable;
            }

            public override int GetHashCode()
            {
                throw ExceptionUtilities.Unreachable;
            }
        }

        private readonly Solution baseSolution;

        // signalled when the session is terminated:
        private readonly CancellationTokenSource cancellation;

        // document id -> [active statements ordered by position]
        private readonly IReadOnlyDictionary<DocumentId, ImmutableArray<ActiveStatementSpan>> baseActiveStatements;

        private readonly DebuggingSession debuggingSession;

        /// <summary>
        /// Stopped at exception, an unwind is required before EnC is allowed. All edits are rude.
        /// </summary>
        private readonly bool stoppedAtException;

        // Results of changed documents analysis. 
        // The work is triggered by an incremental analyzer on idle or explicitly when "continue" operation is executed.
        // Contains analyses of the latest observed document versions.
        private readonly object analysesGuard = new object();
        private readonly Dictionary<DocumentId, Analysis> analyses;

        // A document id is added whenever any analysis reports rude edits.
        private readonly object documentsWithReportedRudeEditsGuard = new object();
        private readonly HashSet<DocumentId> documentsWithReportedRudeEdits;

        private readonly ImmutableDictionary<ProjectId, ProjectReadOnlyReason> projects;

        // EncEditSessionInfo is populated on a background thread and then read from the UI thread
        private readonly object encEditSessionInfoGuard = new object();
        private EncEditSessionInfo encEditSessionInfo = new EncEditSessionInfo();

        internal EditSession(
            Solution baseSolution,
            IReadOnlyDictionary<DocumentId, ImmutableArray<ActiveStatementSpan>> baseActiveStatements,
            DebuggingSession debuggingSession,
            ImmutableDictionary<ProjectId, ProjectReadOnlyReason> projects,
            bool stoppedAtException)
        {
            Debug.Assert(baseSolution != null);
            Debug.Assert(debuggingSession != null);

            this.baseSolution = baseSolution;
            this.debuggingSession = debuggingSession;
            this.stoppedAtException = stoppedAtException;
            this.projects = projects;
            this.cancellation = new CancellationTokenSource();

            // TODO: small dict, pool?
            this.analyses = new Dictionary<DocumentId, Analysis>();
            this.baseActiveStatements = baseActiveStatements;

            // TODO: small dict, pool?
            this.documentsWithReportedRudeEdits = new HashSet<DocumentId>();
        }

        internal CancellationTokenSource Cancellation
        {
            get { return cancellation; }
        }

        internal Solution BaseSolution
        {
            get
            {
                return baseSolution;
            }
        }

        internal IReadOnlyDictionary<DocumentId, ImmutableArray<ActiveStatementSpan>> BaseActiveStatements
        {
            get
            {
                return baseActiveStatements;
            }
        }

        private Solution CurrentSolution
        {
            get
            {
                return baseSolution.Workspace.CurrentSolution;
            }
        }

        public bool StoppedAtException
        {
            get { return stoppedAtException; }
        }

        public IReadOnlyDictionary<ProjectId, ProjectReadOnlyReason> Projects
        {
            get { return projects; }
        }

        internal bool TryGetProjectState(string projectName, out ProjectReadOnlyReason reason)
        {
            // We used to keep track of project ids but Venus may have multiple project ids during debugging sessions,
            // causing EnC fail to recognize they belong to the same project. Therefore, instead of ids,
            // their public display name (which is shared between multiple projects in Venus) is compared.
            foreach (var pair in Projects.Where((p) => baseSolution.GetProject(p.Key)?.Name == projectName))
            {
                reason = pair.Value;
                return true;
            }

            reason = ProjectReadOnlyReason.NotLoaded;
            return false;
        }

        internal bool HasProject(ProjectId id)
        {
            ProjectReadOnlyReason reason;
            return Projects.TryGetValue(id, out reason);
        }

        private List<ValueTuple<DocumentId, AsyncLazy<DocumentAnalysisResults>>> GetChangedDocumentsAnalyses(Project baseProject, Project project)
        {
            var changes = project.GetChanges(baseProject);
            var changedDocuments = changes.GetChangedDocuments().Concat(changes.GetAddedDocuments());
            var result = new List<ValueTuple<DocumentId, AsyncLazy<DocumentAnalysisResults>>>();

            lock (analysesGuard)
            {
                foreach (var changedDocumentId in changedDocuments)
                {
                    result.Add(ValueTuple.Create(changedDocumentId, GetDocumentAnalysisNoLock(project.GetDocument(changedDocumentId))));
                }
            }

            return result;
        }

        private async Task<HashSet<ISymbol>> GetAllAddedSymbols(CancellationToken cancellationToken)
        {
            Analysis[] analyses;
            lock (analysesGuard)
            {
                analyses = this.analyses.Values.ToArray();
            }

            HashSet<ISymbol> addedSymbols = null;
            foreach (var analysis in analyses)
            {
                var results = await analysis.Results.GetValueAsync(cancellationToken).ConfigureAwait(false);
                foreach (var edit in results.SemanticEdits)
                {
                    if (edit.Kind == SemanticEditKind.Insert)
                    {
                        if (addedSymbols == null)
                        {
                            addedSymbols = new HashSet<ISymbol>();
                        }

                        addedSymbols.Add(edit.NewSymbol);
                    }
                }
            }

            return addedSymbols;
        }

        public AsyncLazy<DocumentAnalysisResults> GetDocumentAnalysis(Document document)
        {
            lock (analysesGuard)
            {
                return GetDocumentAnalysisNoLock(document);
            }
        }

        private AsyncLazy<DocumentAnalysisResults> GetDocumentAnalysisNoLock(Document document)
        {
            Analysis analysis;
            if (analyses.TryGetValue(document.Id, out analysis) && analysis.Document == document)
            {
                return analysis.Results;
            }

            var analyzer = document.Project.LanguageServices.GetService<IEditAndContinueAnalyzer>();

            ImmutableArray<ActiveStatementSpan> activeStatements;
            if (!baseActiveStatements.TryGetValue(document.Id, out activeStatements))
            {
                activeStatements = ImmutableArray.Create<ActiveStatementSpan>();
            }

            var lazyResults = new AsyncLazy<DocumentAnalysisResults>(
                asynchronousComputeFunction: async cancellationToken =>
                {
                    try
                    {
                        var result = await analyzer.AnalyzeDocumentAsync(baseSolution, activeStatements, document, cancellationToken).ConfigureAwait(false);

                        if (!result.RudeEditErrors.IsDefault)
                        {
                            lock (documentsWithReportedRudeEditsGuard)
                            {
                                documentsWithReportedRudeEdits.Add(document.Id);
                            }
                        }

                        return result;
                    }
                    catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
                    {
                        throw ExceptionUtilities.Unreachable;
                    }
                },
                cacheResult: true);

            analyses[document.Id] = new Analysis(document, lazyResults);
            return lazyResults;
        }

        internal ImmutableArray<DocumentId> GetDocumentsWithReportedRudeEdits()
        {
            lock (documentsWithReportedRudeEditsGuard)
            {
                return ImmutableArray.CreateRange(documentsWithReportedRudeEdits);
            }
        }

        public async Task<ProjectAnalysisSummary> GetProjectAnalysisSummaryAsync(Project project, CancellationToken cancellationToken)
        {
            try
            {
                var baseProject = baseSolution.GetProject(project.Id);

                var documentAnalyses = GetChangedDocumentsAnalyses(baseProject, project);
                if (documentAnalyses.Count == 0)
                {
                    return ProjectAnalysisSummary.NoChanges;
                }

                bool hasChanges = false;
                bool hasSignificantChanges = false;

                foreach (var analysis in documentAnalyses)
                {
                    var result = await analysis.Item2.GetValueAsync(cancellationToken).ConfigureAwait(false);

                    // skip documents that actually were not changed:
                    if (!result.HasChanges)
                    {
                        continue;
                    }

                    // rude edit detection wasn't completed due to errors in compilation:
                    if (result.HasChangesAndCompilationErrors)
                    {
                        return ProjectAnalysisSummary.CompilationErrors;
                    }

                    // rude edits detected:
                    if (result.RudeEditErrors.Length != 0)
                    {
                        return ProjectAnalysisSummary.RudeEdits;
                    }

                    hasChanges = true;
                    hasSignificantChanges |= result.HasSignificantChanges;
                }

                if (!hasChanges)
                {
                    // we get here if a document is closed and reopen without any actual change:
                    return ProjectAnalysisSummary.NoChanges;
                }

                if (stoppedAtException)
                {
                    // all edits are disallowed when stopped at exception:
                    return ProjectAnalysisSummary.RudeEdits;
                }

                return hasSignificantChanges ?
                    ProjectAnalysisSummary.ValidChanges :
                    ProjectAnalysisSummary.ValidInsignificantChanges;
            }
            catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
            {
                throw ExceptionUtilities.Unreachable;
            }
        }

        private async Task<ProjectChanges> GetProjectChangesAsync(Project project, CancellationToken cancellationToken)
        {
            try
            {
                var baseProject = baseSolution.GetProject(project.Id);
                var allEdits = new List<SemanticEdit>();
                var allLineEdits = new List<KeyValuePair<DocumentId, ImmutableArray<LineChange>>>();

                foreach (var analysis in GetChangedDocumentsAnalyses(baseProject, project))
                {
                    var documentId = analysis.Item1;
                    var result = await analysis.Item2.GetValueAsync(cancellationToken).ConfigureAwait(false);

                    // we shouldn't be asking for deltas in presence of errors:
                    Debug.Assert(!result.HasChangesAndErrors);

                    allEdits.AddRange(result.SemanticEdits);
                    if (result.LineEdits.Length > 0)
                    {
                        allLineEdits.Add(KeyValuePair.Create(documentId, result.LineEdits));
                    }
                }

                // Ideally we shouldn't be asking for deltas in absence of significant changes.
                // But in VS we have no way of telling the debugger that the changes made 
                // to the source are not significant. So we emit an empty delta.
                // Debug.Assert(allEdits.Count > 0 || allLineEdits.Count > 0);

                return new ProjectChanges(allEdits, allLineEdits);
            }
            catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
            {
                throw ExceptionUtilities.Unreachable;
            }
        }

        public async Task<Deltas> EmitProjectDeltaAsync(Project project, EmitBaseline baseline, CancellationToken cancellationToken)
        {
            try
            {
                Debug.Assert(!stoppedAtException);

                var changes = await GetProjectChangesAsync(project, cancellationToken).ConfigureAwait(false);
                var currentCompilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                var allAddedSymbols = await GetAllAddedSymbols(cancellationToken).ConfigureAwait(false);

                var pdbStream = new MemoryStream();
                var updatedMethods = new List<MethodDefinitionHandle>();

                using (var metadataStream = SerializableBytes.CreateWritableStream())
                using (var ilStream = SerializableBytes.CreateWritableStream())
                {
                    EmitDifferenceResult result = currentCompilation.EmitDifference(
                        baseline,
                        changes.SemanticEdits,
                        s => allAddedSymbols?.Contains(s) ?? false,
                        metadataStream,
                        ilStream,
                        pdbStream,
                        updatedMethods,
                        cancellationToken);

                    int[] updateMethodTokens = updatedMethods.Select(h => MetadataTokens.GetToken(h)).ToArray();
                    return new Deltas(ilStream.ToArray(), metadataStream.ToArray(), updateMethodTokens, pdbStream, changes.LineChanges, result);
                }
            }
            catch (Exception e) when (FatalError.ReportUnlessCanceled(e))
            {
                throw ExceptionUtilities.Unreachable;
            }
        }

        internal void LogRudeEditErrors(ImmutableArray<RudeEditDiagnostic> rudeEditErrors)
        {
            lock (encEditSessionInfoGuard)
            {
                if (encEditSessionInfo != null)
                {
                    foreach (var item in rudeEditErrors)
                    {
                        encEditSessionInfo.LogRudeEdit((ushort)item.Kind, item.SyntaxKind);
                    }
                }
            }
        }

        internal void LogEmitProjectDeltaErrors(IEnumerable<string> errorIds)
        {
            lock (encEditSessionInfoGuard)
            {
                Debug.Assert(encEditSessionInfo != null);
                encEditSessionInfo.EmitDeltaErrorIds = errorIds;
            }
        }

        internal void LogBuildState(ProjectAnalysisSummary lastEditSessionSummary)
        {
            lock (encEditSessionInfoGuard)
            {
                Debug.Assert(encEditSessionInfo != null);
                encEditSessionInfo.HadCompilationErrors |= lastEditSessionSummary == ProjectAnalysisSummary.CompilationErrors;
                encEditSessionInfo.HadRudeEdits |= lastEditSessionSummary == ProjectAnalysisSummary.RudeEdits;
                encEditSessionInfo.HadValidChanges |= lastEditSessionSummary == ProjectAnalysisSummary.ValidChanges;
                encEditSessionInfo.HadValidInsignificantChanges |= lastEditSessionSummary == ProjectAnalysisSummary.ValidInsignificantChanges;
            }
        }

        internal void LogEditSession(EncDebuggingSessionInfo encDebuggingSessionInfo)
        {
            lock (encEditSessionInfoGuard)
            {
                Debug.Assert(encEditSessionInfo != null);
                encDebuggingSessionInfo.EndEditSession(this.encEditSessionInfo);
                this.encEditSessionInfo = null;
            }
        }
    }
}