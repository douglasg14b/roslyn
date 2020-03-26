﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Microsoft.VisualStudio.Utilities.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Roslyn.Utilities;
using StreamJsonRpc;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.LanguageService
{
    /// <summary>
    /// Defines the language server to be hooked up to an <see cref="ILanguageClient"/> using StreamJsonRpc.
    /// This runs in proc as not all features provided by this server are available out of proc (e.g. some diagnostics).
    /// </summary>
    internal class InProcLanguageServer
    {
        private readonly IDiagnosticService _diagnosticService;
        private readonly string? _clientName;
        private readonly JsonRpc _jsonRpc;
        private readonly LanguageServerProtocol _protocol;
        private readonly Workspace _workspace;

        private VSClientCapabilities? _clientCapabilities;

        public InProcLanguageServer(Stream inputStream, Stream outputStream, LanguageServerProtocol protocol,
            Workspace workspace, IDiagnosticService diagnosticService, string? clientName)
        {
            _protocol = protocol;
            _workspace = workspace;

            _jsonRpc = new JsonRpc(outputStream, inputStream, this);
            _jsonRpc.StartListening();

            _diagnosticService = diagnosticService;
            _clientName = clientName;
            _diagnosticService.DiagnosticsUpdated += DiagnosticService_DiagnosticsUpdated;
        }

        /// <summary>
        /// Handle the LSP initialize request by storing the client capabilities
        /// and responding with the server capabilities.
        /// The specification assures that the initialize request is sent only once.
        /// </summary>
        [JsonRpcMethod(Methods.InitializeName)]
        public Task<InitializeResult> InitializeAsync(JToken input, CancellationToken cancellationToken)
        {
            // The VS LSP protocol package changed the type of 'tagSupport' from bool to an object.
            // Our version of the LSP protocol package is older and assumes that the type is bool, so deserialization fails.
            // Since we don't really read this field, just no-op the error until we can update our package references.
            // https://github.com/dotnet/roslyn/issues/40829 tracks updating this.
            var settings = new JsonSerializerSettings
            {
                Error = (sender, args) =>
                {
                    if (object.Equals(args.ErrorContext.Member, "tagSupport") && args.ErrorContext.OriginalObject.GetType() == typeof(PublishDiagnosticsSetting))
                    {
                        args.ErrorContext.Handled = true;
                    }
                }
            };
            var serializer = JsonSerializer.Create(settings);

            // InitializeParams only references ClientCapabilities, but the VS LSP client
            // sends additional VS specific capabilities, so directly deserialize them into the VSClientCapabilities
            // to avoid losing them.
            _clientCapabilities = input["capabilities"].ToObject<VSClientCapabilities>(serializer);
            return _protocol.InitializeAsync(_workspace.CurrentSolution, input.ToObject<InitializeParams>(serializer), _clientCapabilities, cancellationToken);
        }

        [JsonRpcMethod(Methods.InitializedName)]
        public async Task InitializedAsync()
        {
            // Publish diagnostics for all open documents immediately following initialization.
            var solution = _workspace.CurrentSolution;
            var openDocuments = _workspace.GetOpenDocumentIds();
            foreach (var documentId in openDocuments)
            {
                var document = solution.GetDocument(documentId);
                if (document != null)
                {
                    await PublishDiagnosticsAsync(document).ConfigureAwait(false);
                }
            }
        }

        [JsonRpcMethod(Methods.ShutdownName)]
        public object? Shutdown(CancellationToken _) => null;

        [JsonRpcMethod(Methods.ExitName)]
        public void Exit()
        {
        }

        [JsonRpcMethod(Methods.TextDocumentDefinitionName)]
        public Task<object> GetTextDocumentDefinitionAsync(JToken input, CancellationToken cancellationToken)
        {
            var textDocumentPositionParams = input.ToObject<TextDocumentPositionParams>();
            return _protocol.GoToDefinitionAsync(_workspace.CurrentSolution, textDocumentPositionParams, _clientCapabilities, cancellationToken);
        }

        [JsonRpcMethod(Methods.TextDocumentRenameName)]
        public Task<WorkspaceEdit> GetTextDocumentRenameAsync(JToken input, CancellationToken cancellationToken)
        {
            var renameParams = input.ToObject<RenameParams>();
            return _protocol.RenameAsync(_workspace.CurrentSolution, renameParams, _clientCapabilities, cancellationToken);
        }

        [JsonRpcMethod(Methods.TextDocumentCompletionName)]
        public Task<object> GetTextDocumentCompletionAsync(JToken input, CancellationToken cancellationToken)
        {
            var completionParams = input.ToObject<CompletionParams>();
            return _protocol.GetCompletionsAsync(_workspace.CurrentSolution, completionParams, _clientCapabilities, cancellationToken);
        }

        [JsonRpcMethod(Methods.TextDocumentCompletionResolveName)]
        public Task<CompletionItem> ResolveCompletionItemAsync(JToken input, CancellationToken cancellationToken)
        {
            var completionItem = input.ToObject<CompletionItem>();
            return _protocol.ResolveCompletionItemAsync(_workspace.CurrentSolution, completionItem, _clientCapabilities, cancellationToken);
        }

        [JsonRpcMethod(Methods.TextDocumentDocumentHighlightName)]
        public Task<DocumentHighlight[]> GetTextDocumentDocumentHighlightsAsync(JToken input, CancellationToken cancellationToken)
        {
            var textDocumentPositionParams = input.ToObject<TextDocumentPositionParams>();
            return _protocol.GetDocumentHighlightAsync(_workspace.CurrentSolution, textDocumentPositionParams, _clientCapabilities, cancellationToken);
        }

        [JsonRpcMethod(Methods.TextDocumentDocumentSymbolName)]
        public Task<object[]> GetTextDocumentDocumentSymbolsAsync(JToken input, CancellationToken cancellationToken)
        {
            var documentSymbolParams = input.ToObject<DocumentSymbolParams>();
            return _protocol.GetDocumentSymbolsAsync(_workspace.CurrentSolution, documentSymbolParams, _clientCapabilities, cancellationToken);
        }

        [JsonRpcMethod(Methods.TextDocumentFormattingName)]
        public Task<TextEdit[]> GetTextDocumentFormattingAsync(JToken input, CancellationToken cancellationToken)
        {
            var documentFormattingParams = input.ToObject<DocumentFormattingParams>();
            return _protocol.FormatDocumentAsync(_workspace.CurrentSolution, documentFormattingParams, _clientCapabilities, cancellationToken);
        }

        [JsonRpcMethod(Methods.TextDocumentOnTypeFormattingName)]
        public Task<TextEdit[]> GetTextDocumentFormattingOnTypeAsync(JToken input, CancellationToken cancellationToken)
        {
            var documentOnTypeFormattingParams = input.ToObject<DocumentOnTypeFormattingParams>();
            return _protocol.FormatDocumentOnTypeAsync(_workspace.CurrentSolution, documentOnTypeFormattingParams, _clientCapabilities, cancellationToken);
        }

        [JsonRpcMethod(Methods.TextDocumentImplementationName)]
        public Task<object> GetTextDocumentImplementationsAsync(JToken input, CancellationToken cancellationToken)
        {
            var textDocumentPositionParams = input.ToObject<TextDocumentPositionParams>();
            return _protocol.FindImplementationsAsync(_workspace.CurrentSolution, textDocumentPositionParams, _clientCapabilities, cancellationToken);
        }

        [JsonRpcMethod(Methods.TextDocumentRangeFormattingName)]
        public Task<TextEdit[]> GetTextDocumentRangeFormattingAsync(JToken input, CancellationToken cancellationToken)
        {
            var documentRangeFormattingParams = input.ToObject<DocumentRangeFormattingParams>();
            return _protocol.FormatDocumentRangeAsync(_workspace.CurrentSolution, documentRangeFormattingParams, _clientCapabilities, cancellationToken);
        }

        [JsonRpcMethod(Methods.TextDocumentSignatureHelpName)]
        public async Task<SignatureHelp> GetTextDocumentSignatureHelpAsync(JToken input, CancellationToken cancellationToken)
        {
            var textDocumentPositionParams = input.ToObject<TextDocumentPositionParams>();
            return await _protocol.GetSignatureHelpAsync(_workspace.CurrentSolution, textDocumentPositionParams, _clientCapabilities, cancellationToken).ConfigureAwait(false);
        }

        [JsonRpcMethod(Methods.WorkspaceSymbolName)]
        public async Task<SymbolInformation[]> GetWorkspaceSymbolsAsync(JToken input, CancellationToken cancellationToken)
        {
            var workspaceSymbolParams = input.ToObject<WorkspaceSymbolParams>();
            return await _protocol.GetWorkspaceSymbolsAsync(_workspace.CurrentSolution, workspaceSymbolParams, _clientCapabilities, cancellationToken).ConfigureAwait(false);
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void DiagnosticService_DiagnosticsUpdated(object sender, DiagnosticsUpdatedArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            // Since this is an async void method, exceptions here will crash the host VS. We catch exceptions here to make sure that we don't crash the host since
            // the worst outcome here is that guests may not see all diagnostics.
            try
            {
                // LSP doesnt support diagnostics without a document. So if we get project level diagnostics without a document, ignore them.
                if (e.DocumentId != null && e.Solution != null)
                {
                    var document = e.Solution.GetDocument(e.DocumentId);
                    if (document == null || document.FilePath == null)
                    {
                        return;
                    }

                    // Only publish document diagnostics for the languages this provider supports.
                    if (document.Project.Language != LanguageNames.CSharp && document.Project.Language != LanguageNames.VisualBasic)
                    {
                        return;
                    }

                    // LSP does not currently support publishing diagnostics incrememntally, so we re-publish all diagnostics.
                    await PublishDiagnosticsAsync(document).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (FatalError.ReportWithoutCrash(ex))
            {
            }
        }

        /// <summary>
        /// Stores the last published LSP diagnostics with the Roslyn document that they came from.
        /// This is useful in the following scenario.  Imagine we have documentA which has contributions from mapped files m1 and m2.
        /// dA -> m1
        /// And m1 additionally contributes to documentB.
        /// m1 -> dA, dB
        /// When we query for diagnostic on dA, we get a subset of the diagnostics on m1 (missing the contributions from dB)
        /// Since each publish diagnostics notification replaces diagnostics per document,
        /// we must union the diagnostics contribution from dB and dA to produce all diagnostics for m1 and publish all at once.
        /// 
        /// This dictionary stores the previously computed diagnostics for the published file so that we can
        /// union the currently computed diagnostics (e.g. for dA) with previously computed diagnostics (e.g. from dB).
        /// </summary>
        private readonly Dictionary<Uri, Dictionary<DocumentId, ImmutableArray<LanguageServer.Protocol.Diagnostic>>> _publishedFileToDiagnostics =
            new Dictionary<Uri, Dictionary<DocumentId, ImmutableArray<LanguageServer.Protocol.Diagnostic>>>();

        /// <summary>
        /// Stores the mapping of a document to the uri(s) of diagnostics previously produced for this document.
        /// When we get empty diagnostics for the document we need to find the uris we previously published for this document.
        /// Then we can publish the updated diagnostics set for those uris (either empty or the diagnostic contributions from other documents).
        /// </summary>
        private readonly Dictionary<DocumentId, ImmutableArray<Uri>> _documentsToPublishedUris = new Dictionary<DocumentId, ImmutableArray<Uri>>();

        private async Task PublishDiagnosticsAsync(Document document)
        {
            // Retrieve all diagnostics for the current document grouped by their actual file uri.
            var fileUriToDiagnostics = await GetDiagnosticsAsync(document, CancellationToken.None).ConfigureAwait(false);

            // Get the list of file uris with diagnostics (for the document).
            // If we found no diagnostics use previously published uris so we can clear out diagnostics from this document.
            var urisForCurrentDocument = fileUriToDiagnostics.Keys.Any() ? fileUriToDiagnostics.Keys.ToImmutableArray()
                : GetOrEmpty(_documentsToPublishedUris, document.Id);

            // Go through each uri and publish the updated set of diagnostics per uri.
            foreach (var fileUri in urisForCurrentDocument)
            {
                // Get the updated diagnostics for a single uri that were contributed by the current document.
                var diagnostics = GetOrEmpty(fileUriToDiagnostics, fileUri);

                if (_publishedFileToDiagnostics.ContainsKey(fileUri))
                {
                    // Get all previously published diagnostics for this uri excluding those that were contributed from the current document.
                    // We don't need those since we just computed the updated values above.
                    var diagnosticsFromOtherDocuments = _publishedFileToDiagnostics[fileUri].Where(kvp => kvp.Key != document.Id).SelectMany(kvp => kvp.Value);

                    // Since diagnostics are replaced per uri, we must publish both contributions from this document and any other document
                    // that has diagnostic contributions to this uri, so union the two sets.
                    diagnostics = diagnostics.AddRange(diagnosticsFromOtherDocuments);
                }

                await SendDiagnosticsNotificationAsync(fileUri, diagnostics).ConfigureAwait(false);

                // Update the published diagnostics map to contain the new diagnostics for this document and mapped uri.
                _publishedFileToDiagnostics.GetOrAdd(fileUri,
                    (_) => { return new Dictionary<DocumentId, ImmutableArray<LanguageServer.Protocol.Diagnostic>>(); })[document.Id] = diagnostics;
            }

            // Update the mapping for this document to be the uris we just published diagnostics for.
            _documentsToPublishedUris[document.Id] = urisForCurrentDocument;
        }

        private async Task SendDiagnosticsNotificationAsync(Uri uri, ImmutableArray<LanguageServer.Protocol.Diagnostic> diagnostics)
        {
            var publishDiagnosticsParams = new PublishDiagnosticParams { Diagnostics = diagnostics.ToArray(), Uri = uri };
            await _jsonRpc.NotifyWithParameterObjectAsync(Methods.TextDocumentPublishDiagnosticsName, publishDiagnosticsParams).ConfigureAwait(false);
        }

        private async Task<Dictionary<Uri, ImmutableArray<LanguageServer.Protocol.Diagnostic>>> GetDiagnosticsAsync(Document document, CancellationToken cancellationToken)
        {
            var diagnostics = _diagnosticService.GetDiagnostics(document.Project.Solution.Workspace, document.Project.Id, document.Id, null, false, cancellationToken)
                                                .Where(IncludeDiagnostic);

            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

            // Razor documents can import other razor documents
            // https://docs.microsoft.com/en-us/aspnet/core/mvc/views/layout?view=aspnetcore-3.1#importing-shared-directives
            // https://docs.microsoft.com/en-us/aspnet/core/blazor/layouts?view=aspnetcore-3.1#centralized-layout-selection
            // The imported files contents are added to the content of the generated C# file, so we get diagnostics
            // for both the c# contents in the original razor document and for any of the content in any of the imported
            // files when we query diagnostics for the generated C# file.
            // These diagnostics will be reported with DiagnosticDataLocation.OriginalFilePath = generated C# file and
            // DiagnosticDataLocation.MappedFilePath = imported razor file.
            // This means that in general we could have diagnostics produced by one generated file that map to many different actual razor files.
            // We can't filter them out as we don't know which razor file(s) the underlying generated C# document actually maps to
            // and which are just imported.  So we publish them all and let them get de-duped.
            var fileUriToDiagnostics = diagnostics.GroupBy(diagnostic => GetDiagnosticUri(document, diagnostic)).ToDictionary(
                group => group.Key,
                group => group.Select(diagnostic => ConvertToLspDiagnostic(diagnostic, text)).ToImmutableArray());
            return fileUriToDiagnostics;

            static Uri GetDiagnosticUri(Document document, DiagnosticData diagnosticData)
            {
                Contract.ThrowIfNull(diagnosticData.DataLocation, "Diagnostic data location should not be null here");

                var filePath = diagnosticData.DataLocation.MappedFilePath ?? diagnosticData.DataLocation.OriginalFilePath;
                return ProtocolConversions.GetUriFromFilePath(filePath);
            }

            static LanguageServer.Protocol.Diagnostic ConvertToLspDiagnostic(DiagnosticData diagnosticData, SourceText text)
            {
                return new LanguageServer.Protocol.Diagnostic
                {
                    Code = diagnosticData.Id,
                    Message = diagnosticData.Message,
                    Severity = ProtocolConversions.DiagnosticSeverityToLspDiagnositcSeverity(diagnosticData.Severity),
                    Range = GetDiagnosticRange(diagnosticData.DataLocation, text),
                    // Only the unnecessary diagnostic tag is currently supported via LSP.
                    Tags = diagnosticData.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary)
                        ? new DiagnosticTag[] { DiagnosticTag.Unnecessary }
                        : Array.Empty<DiagnosticTag>()
                };
            }
        }

        private bool IncludeDiagnostic(DiagnosticData diagnostic)
        {
            if (!diagnostic.Properties.TryGetValue(nameof(DocumentPropertiesService.DiagnosticsLspClientName), out var diagnosticClientName))
            {
                // This diagnostic is not restricted to a specific LSP client, so just pass it through
                return true;
            }

            // We only include this diagnostic if it directly matches our name.
            return diagnosticClientName == _clientName;
        }

        private static LanguageServer.Protocol.Range? GetDiagnosticRange(DiagnosticDataLocation? diagnosticDataLocation, SourceText text)
        {
            var linePositionSpan = DiagnosticData.GetLinePositionSpan(diagnosticDataLocation, text, useMapped: true);
            return ProtocolConversions.LinePositionToRange(linePositionSpan);
        }

        private static ImmutableArray<V> GetOrEmpty<K, V>(Dictionary<K, ImmutableArray<V>> dict, K key)
        {
            if (dict.TryGetValue(key, out var immutableArray))
            {
                return immutableArray;
            }

            return ImmutableArray<V>.Empty;
        }
    }
}
