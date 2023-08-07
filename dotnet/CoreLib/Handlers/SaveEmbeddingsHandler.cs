﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticMemory.Client;
using Microsoft.SemanticMemory.Core.AppBuilders;
using Microsoft.SemanticMemory.Core.ContentStorage;
using Microsoft.SemanticMemory.Core.Diagnostics;
using Microsoft.SemanticMemory.Core.MemoryStorage;
using Microsoft.SemanticMemory.Core.Pipeline;

namespace Microsoft.SemanticMemory.Core.Handlers;

public class SaveEmbeddingsHandler : IPipelineStepHandler
{
    private readonly IPipelineOrchestrator _orchestrator;
    private readonly List<ISemanticMemoryVectorDb> _vectorDbs = new();
    private readonly ILogger<SaveEmbeddingsHandler> _log;

    /// <summary>
    /// Handler responsible for copying embeddings from storage to list of vector DBs
    /// Note: stepName and other params are injected with DI
    /// </summary>
    /// <param name="stepName">Pipeline step for which the handler will be invoked</param>
    /// <param name="orchestrator">Current orchestrator used by the pipeline, giving access to content and other helps.</param>
    /// <param name="serviceProvider">.NET service provider</param>
    /// <param name="log">Application logger</param>
    public SaveEmbeddingsHandler(
        string stepName,
        IPipelineOrchestrator orchestrator,
        IServiceProvider serviceProvider,
        ILogger<SaveEmbeddingsHandler>? log = null)
    {
        this.StepName = stepName;
        this._orchestrator = orchestrator;
        this._log = log
                    ?? serviceProvider.GetService<ILogger<SaveEmbeddingsHandler>>()
                    ?? DefaultLogger<SaveEmbeddingsHandler>.Instance;

        var vectorDbBuilders = serviceProvider.GetService<ConfiguredServices<ISemanticMemoryVectorDb>>()
                               ?? throw new SemanticMemoryException("List of embedding generators not configured");
        foreach (Func<IServiceProvider, ISemanticMemoryVectorDb> x in vectorDbBuilders.GetList())
        {
            this._vectorDbs.Add(x.Invoke(serviceProvider));
        }

        this._log.LogInformation("Handler {0} ready, {1} vector storages", stepName, this._vectorDbs.Count);
        if (this._vectorDbs.Count < 1)
        {
            this._log.LogWarning("No vector storage configured");
        }
    }

    /// <inheritdoc />
    public string StepName { get; }

    /// <inheritdoc />
    public async Task<(bool success, DataPipeline updatedPipeline)> InvokeAsync(DataPipeline pipeline, CancellationToken cancellationToken = default)
    {
        await this.DeletePreviousEmbeddingsAsync(pipeline, cancellationToken).ConfigureAwait(false);
        pipeline.PreviousExecutionsToPurge = new List<DataPipeline>();

        // For each embedding file => For each Vector DB => Store vector (collections ==> tags)
        foreach (KeyValuePair<string, DataPipeline.GeneratedFileDetails> embeddingFile in pipeline.Files.SelectMany(x => x.GeneratedFiles.Where(f => f.Value.IsEmbeddingFile())))
        {
            string vectorJson = await this._orchestrator.ReadTextFileAsync(pipeline, embeddingFile.Value.Name, cancellationToken).ConfigureAwait(false);
            EmbeddingFileContent? embeddingData = JsonSerializer.Deserialize<EmbeddingFileContent>(vectorJson);
            if (embeddingData == null)
            {
                throw new OrchestrationException($"Unable to deserialize embedding file {embeddingFile.Value.Name}");
            }

            var record = new MemoryRecord
            {
                Id = GetEmbeddingRecordId(pipeline.UserId, pipeline.Id, embeddingFile.Value.Id),
                Vector = embeddingData.Vector,
                Owner = pipeline.UserId,
            };

            // Note that the User Id is not set here, but when mapping MemoryRecord to the specific VectorDB schema 
            record.Tags.Add(Constants.ReservedPipelineIdTag, pipeline.Id);
            record.Tags.Add(Constants.ReservedFileIdTag, embeddingFile.Value.ParentId);
            record.Tags.Add(Constants.ReservedFilePartitionTag, embeddingFile.Value.Id);
            record.Tags.Add(Constants.ReservedFileTypeTag, pipeline.GetFile(embeddingFile.Value.ParentId).Type);

            pipeline.Tags.CopyTo(record.Tags);

            record.Metadata.Add("file_name", pipeline.GetFile(embeddingFile.Value.ParentId).Name);
            record.Metadata.Add("vector_provider", embeddingData.GeneratorProvider);
            record.Metadata.Add("vector_generator", embeddingData.GeneratorName);
            record.Metadata.Add("last_update", DateTimeOffset.UtcNow.ToString("s"));

            // Store text partition for RAG
            // TODO: make this optional to reduce space usage, using blob files instead
            string partitionContent = await this._orchestrator.ReadTextFileAsync(pipeline, embeddingData.SourceFileName, cancellationToken).ConfigureAwait(false);
            record.Metadata.Add("text", partitionContent);

            string indexName = record.Owner;

            foreach (ISemanticMemoryVectorDb client in this._vectorDbs)
            {
                this._log.LogTrace("Creating index '{0}'", indexName);
                await client.CreateIndexAsync(indexName, record.Vector.Count, cancellationToken).ConfigureAwait(false);

                this._log.LogTrace("Saving record {0} in index '{1}'", record.Id, indexName);
                await client.UpsertAsync(indexName, record, cancellationToken).ConfigureAwait(false);
            }
        }

        return (true, pipeline);
    }

    private async Task DeletePreviousEmbeddingsAsync(DataPipeline pipeline, CancellationToken cancellationToken)
    {
        if (pipeline.PreviousExecutionsToPurge.Count == 0) { return; }

        var embeddingsToKeep = new HashSet<string>();

        // Decide which embeddings not to delete, looking at the current pipeline
        foreach (DataPipeline.GeneratedFileDetails embeddingFile in pipeline.Files.SelectMany(f1 => f1.GeneratedFiles.Where(f2 => f2.Value.IsEmbeddingFile()).Select(x => x.Value)))
        {
            string recordId = GetEmbeddingRecordId(pipeline.UserId, pipeline.Id, embeddingFile.Id);
            embeddingsToKeep.Add(recordId);
        }

        // Purge old pipelines data, unless it's still relevant in the current pipeline
        foreach (DataPipeline oldPipeline in pipeline.PreviousExecutionsToPurge)
        {
            foreach (DataPipeline.GeneratedFileDetails embeddingFile in oldPipeline.Files.SelectMany(f1 => f1.GeneratedFiles.Where(f2 => f2.Value.IsEmbeddingFile()).Select(x => x.Value)))
            {
                string recordId = GetEmbeddingRecordId(pipeline.UserId, oldPipeline.Id, embeddingFile.Id);
                if (embeddingsToKeep.Contains(recordId)) { continue; }

                string indexName = pipeline.UserId;

                foreach (ISemanticMemoryVectorDb client in this._vectorDbs)
                {
                    this._log.LogTrace("Deleting old embedding {0}", recordId);
                    await client.DeleteAsync(indexName, new MemoryRecord { Id = recordId }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static string GetEmbeddingRecordId(string userId, string pipelineId, string filePartitionId)
    {
        return $"usr={userId}//ppl={pipelineId}//prt={filePartitionId}";
    }
}
