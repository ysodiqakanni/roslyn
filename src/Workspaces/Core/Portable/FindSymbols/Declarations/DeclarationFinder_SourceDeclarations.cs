﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.PatternMatching;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.FindSymbols
{
    // All the logic for finding source declarations in a given solution/project with some name 
    // is in this file.  

    internal static partial class DeclarationFinder
    {
        #region Dispatch Members

        // These are the public entrypoints to finding source declarations.  They will attempt to
        // remove the query to the OOP process, and will fallback to local processing if they can't.

        public static async Task<ImmutableArray<SymbolAndProjectId>> FindSourceDeclarationsWithNormalQueryAsync(
            Solution solution, string name, bool ignoreCase, SymbolFilter criteria, CancellationToken cancellationToken)
        {
            if (solution == null)
            {
                throw new ArgumentNullException(nameof(solution));
            }

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return ImmutableArray<SymbolAndProjectId>.Empty;
            }

            var (succeded, results) = await TryFindSourceDeclarationsWithNormalQueryInRemoteProcessAsync(
                solution, name, ignoreCase, criteria, cancellationToken).ConfigureAwait(false);

            if (succeded)
            {
                return results;
            }

            return await FindSourceDeclarationsWithNormalQueryInCurrentProcessAsync(
                solution, name, ignoreCase, criteria, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<ImmutableArray<SymbolAndProjectId>> FindSourceDeclarationsWithNormalQueryAsync(
            Project project, string name, bool ignoreCase, SymbolFilter criteria, CancellationToken cancellationToken)
        {
            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return ImmutableArray<SymbolAndProjectId>.Empty;
            }

            var (succeded, results) = await TryFindSourceDeclarationsWithNormalQueryInRemoteProcessAsync(
                project, name, ignoreCase, criteria, cancellationToken).ConfigureAwait(false);

            if (succeded)
            {
                return results;
            }

            return await FindSourceDeclarationsWithNormalQueryInCurrentProcessAsync(
                project, name, ignoreCase, criteria, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<ImmutableArray<SymbolAndProjectId>> FindSourceDeclarationsWithPatternAsync(
            Project project, string pattern, SymbolFilter criteria, CancellationToken cancellationToken)
        {
            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            if (pattern == null)
            {
                throw new ArgumentNullException(nameof(pattern));
            }

            var (succeded, results) = await TryFindSourceDeclarationsWithPatternInRemoteProcessAsync(
                project, pattern, criteria, cancellationToken).ConfigureAwait(false);

            if (succeded)
            {
                return results;
            }

            return await FindSourceDeclarationsWithPatternInCurrentProcessAsync(
                project, pattern, criteria, cancellationToken).ConfigureAwait(false);
        }

        #endregion

        #region Remote Dispatch

        // These are the members that actually try to send the request to the remote process.

        private static async Task<(bool, ImmutableArray<SymbolAndProjectId>)> TryFindSourceDeclarationsWithNormalQueryInRemoteProcessAsync(
            Solution solution, string name, bool ignoreCase, SymbolFilter criteria, CancellationToken cancellationToken)
        {
            var session = await SymbolFinder.TryGetRemoteSessionAsync(solution, cancellationToken).ConfigureAwait(false);
            if (session != null)
            {
                var result = await session.InvokeAsync<SerializableSymbolAndProjectId[]>(
                    nameof(IRemoteSymbolFinder.FindSolutionSourceDeclarationsWithNormalQueryAsync),
                    name, ignoreCase, criteria).ConfigureAwait(false);

                var rehydrated = await RehydrateAsync(
                    solution, result, cancellationToken).ConfigureAwait(false);

                return (true, rehydrated);
            }

            return (false, ImmutableArray<SymbolAndProjectId>.Empty);
        }

        private static async Task<(bool, ImmutableArray<SymbolAndProjectId>)> TryFindSourceDeclarationsWithNormalQueryInRemoteProcessAsync(
            Project project, string name, bool ignoreCase, SymbolFilter criteria, CancellationToken cancellationToken)
        {
            var session = await SymbolFinder.TryGetRemoteSessionAsync(project.Solution, cancellationToken).ConfigureAwait(false);
            if (session != null)
            {
                var result = await session.InvokeAsync<SerializableSymbolAndProjectId[]>(
                    nameof(IRemoteSymbolFinder.FindProjectSourceDeclarationsWithNormalQueryAsync),
                    project.Id, name, ignoreCase, criteria).ConfigureAwait(false);

                var rehydrated = await RehydrateAsync(
                    project.Solution, result, cancellationToken).ConfigureAwait(false);

                return (true, rehydrated);
            }

            return (false, ImmutableArray<SymbolAndProjectId>.Empty);
        }

        private static async Task<(bool, ImmutableArray<SymbolAndProjectId>)> TryFindSourceDeclarationsWithPatternInRemoteProcessAsync(
            Project project, string pattern, SymbolFilter criteria, CancellationToken cancellationToken)
        {
            var session = await SymbolFinder.TryGetRemoteSessionAsync(project.Solution, cancellationToken).ConfigureAwait(false);
            if (session != null)
            {
                var result = await session.InvokeAsync<SerializableSymbolAndProjectId[]>(
                    nameof(IRemoteSymbolFinder.FindProjectSourceDeclarationsWithPatternAsync),
                    project.Id, pattern, criteria).ConfigureAwait(false);

                var rehydrated = await RehydrateAsync(
                    project.Solution, result, cancellationToken).ConfigureAwait(false);

                return (true, rehydrated);
            }

            return (false, ImmutableArray<SymbolAndProjectId>.Empty);
        }

        #endregion

        #region Local processing

        // These are the members that have the core logic that does the actual finding.  They will
        // be called 'in proc' in the remote process if we are able to remote the request.  Or they
        // will be called 'in proc' from within VS if we are not able to remote the request.

        internal static async Task<ImmutableArray<SymbolAndProjectId>> FindSourceDeclarationsWithNormalQueryInCurrentProcessAsync(
            Solution solution, string name, bool ignoreCase, SymbolFilter criteria, CancellationToken cancellationToken)
        {
            var query = SearchQuery.Create(name, ignoreCase);
            var result = ArrayBuilder<SymbolAndProjectId>.GetInstance();
            foreach (var projectId in solution.ProjectIds)
            {
                var project = solution.GetProject(projectId);
                await AddCompilationDeclarationsWithNormalQueryAsync(
                    project, query, criteria, result, cancellationToken).ConfigureAwait(false);
            }

            return result.ToImmutableAndFree();
        }

        internal static async Task<ImmutableArray<SymbolAndProjectId>> FindSourceDeclarationsWithNormalQueryInCurrentProcessAsync(
            Project project, string name, bool ignoreCase, SymbolFilter filter, CancellationToken cancellationToken)
        {
            var list = ArrayBuilder<SymbolAndProjectId>.GetInstance();
            await AddCompilationDeclarationsWithNormalQueryAsync(
                project, SearchQuery.Create(name, ignoreCase),
                filter, list, cancellationToken).ConfigureAwait(false);
            return list.ToImmutableAndFree();
        }

        internal static async Task<ImmutableArray<SymbolAndProjectId>> FindSourceDeclarationsWithPatternInCurrentProcessAsync(
            Project project, string pattern, SymbolFilter criteria, CancellationToken cancellationToken)
        {
            // The compiler API only supports a predicate which is given a symbol's name.  Because
            // we only have the name, and nothing else, we need to check it against the last segment
            // of the pattern.  i.e. if the pattern is 'Console.WL' and we are given 'WriteLine', then
            // we don't want to check the whole pattern against it (as it will clearly fail), instead
            // we only want to check the 'WL' portion.  Then, after we get all the candidate symbols
            // we'll check if the full name matches the full pattern.
            var dotIndex = pattern.IndexOf('.');
            var isDottedPattern = dotIndex >= 0;

            // If we don't have a dot in the pattern, just make a pattern matcher for the entire
            // pattern they passed in.  Otherwise, make a pattern matcher just for the part after
            // the dot.
            var lastPartPatternMatcher = isDottedPattern
                ? new PatternMatcher(pattern)
                : new PatternMatcher(pattern.Substring(dotIndex + 1));

            var query = SearchQuery.CreateCustom(k =>
                lastPartPatternMatcher.GetFirstMatch(k, includeMatchSpans: false) != null);

            var symbolAndProjectIds = await SymbolFinder.FindSourceDeclarationsWithCustomQueryAsync(
                project, query, criteria, cancellationToken).ConfigureAwait(false);

            if (symbolAndProjectIds.Length == 0)
            {
                return ImmutableArray<SymbolAndProjectId>.Empty;
            }

            if (!isDottedPattern)
            {
                // If it wasn't a dotted pattern, then we're done.  We can just return whatever
                // set of results we got so far.
                return symbolAndProjectIds;
            }

            // Ok, we had a dotted pattern.  Have to see if the symbol's container matches the 
            // pattern as well.
            var entireNamePatternMatcher = new PatternMatcher(pattern);
            return symbolAndProjectIds.WhereAsArray(t =>
                !entireNamePatternMatcher.GetMatches(t.Symbol.Name, GetContainer(t.Symbol)).IsEmpty);
        }

        private static string GetSearchName(ISymbol symbol)
        {
            if (symbol.IsConstructor() || symbol.IsStaticConstructor())
            {
                return symbol.ContainingType.Name;
            }
            else if (symbol.IsIndexer() && symbol.Name == WellKnownMemberNames.Indexer)
            {
                return "this";
            }

            return symbol.Name;
        }

        private static string GetContainer(ISymbol symbol)
        {
            var container = symbol.ContainingSymbol;
            if (container == null)
            {
                return null;
            }

            return container.ToDisplayString(DottedNameFormat);
        }

        private static readonly SymbolDisplayFormat DottedNameFormat =
            new SymbolDisplayFormat(
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                delegateStyle: SymbolDisplayDelegateStyle.NameOnly,
                extensionMethodStyle: SymbolDisplayExtensionMethodStyle.StaticMethod,
                propertyStyle: SymbolDisplayPropertyStyle.NameOnly);

        #endregion
    }
}