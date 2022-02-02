using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using LamarCodeGeneration;
using Marten.Events;
using Marten.Events.Daemon;
using Marten.Events.Daemon.HighWater;
using Marten.Exceptions;
using Marten.Internal.CompiledQueries;
using Marten.Internal.Sessions;
using Marten.Schema;
using Marten.Services;
using Marten.Storage;
using Microsoft.Extensions.Logging;
using Weasel.Core.Migrations;

#nullable enable
namespace Marten
{
    /// <summary>
    ///     The main entry way to using Marten
    /// </summary>
    public partial class DocumentStore: IDocumentStore
    {
        private readonly IMartenLogger _logger;

        /// <summary>
        ///     Creates a new DocumentStore with the supplied StoreOptions
        /// </summary>
        /// <param name="options"></param>
        public DocumentStore(StoreOptions options)
        {
            options.ApplyConfiguration();
            options.Validate();

            Options = options;
            _logger = options.Logger();
            Serializer = options.Serializer();

            if (options.CreateDatabases != null)
            {
                IDatabaseGenerator databaseGenerator = new DatabaseGenerator();
                databaseGenerator.CreateDatabases(Tenancy, options.CreateDatabases);
            }

            Schema = Tenancy.Default.Database;

            Storage.PostProcessConfiguration();
            Events.AssertValidity(this);
            Options.Projections.AssertValidity(this);

            Advanced = new AdvancedOperations(this);

            Diagnostics = new Diagnostics(this);

            options.InitialData.Each(x => x.Populate(this).GetAwaiter().GetResult());
        }

        public ITenancy Tenancy => Options.Tenancy;

        public EventGraph Events => Options.EventGraph;

        public StorageFeatures Storage => Options.Storage;

        public ISerializer Serializer { get; }

        public StoreOptions Options { get; }

        IReadOnlyStoreOptions IDocumentStore.Options => Options;

        public virtual void Dispose()
        {
        }

        public IDatabase Schema { get; }
        public AdvancedOperations Advanced { get; }

        public void BulkInsert<T>(IReadOnlyCollection<T> documents, BulkInsertMode mode = BulkInsertMode.InsertsOnly,
            int batchSize = 1000)
        {
            var bulkInsertion = new BulkInsertion(Tenancy.Default, Options);
            bulkInsertion.BulkInsert(documents, mode, batchSize);
        }

        public void BulkInsertDocuments(IEnumerable<object> documents, BulkInsertMode mode = BulkInsertMode.InsertsOnly,
            int batchSize = 1000)
        {
            var bulkInsertion = new BulkInsertion(Tenancy.Default, Options);
            bulkInsertion.BulkInsertDocuments(documents, mode, batchSize);
        }

        public void BulkInsert<T>(string tenantId, IReadOnlyCollection<T> documents,
            BulkInsertMode mode = BulkInsertMode.InsertsOnly,
            int batchSize = 1000)
        {
            var bulkInsertion = new BulkInsertion(Tenancy.GetTenant(tenantId), Options);
            bulkInsertion.BulkInsert(documents, mode, batchSize);
        }

        public void BulkInsertDocuments(string tenantId, IEnumerable<object> documents,
            BulkInsertMode mode = BulkInsertMode.InsertsOnly,
            int batchSize = 1000)
        {
            var bulkInsertion = new BulkInsertion(Tenancy.GetTenant(tenantId), Options);
            bulkInsertion.BulkInsertDocuments(documents, mode, batchSize);
        }

        public Task BulkInsertAsync<T>(IReadOnlyCollection<T> documents,
            BulkInsertMode mode = BulkInsertMode.InsertsOnly,
            int batchSize = 1000, CancellationToken cancellation = default)
        {
            var bulkInsertion = new BulkInsertion(Tenancy.Default, Options);
            return bulkInsertion.BulkInsertAsync(documents, mode, batchSize, cancellation);
        }

        public Task BulkInsertAsync<T>(string tenantId, IReadOnlyCollection<T> documents,
            BulkInsertMode mode = BulkInsertMode.InsertsOnly, int batchSize = 1000,
            CancellationToken cancellation = default)
        {
            var bulkInsertion = new BulkInsertion(Tenancy.GetTenant(tenantId), Options);
            return bulkInsertion.BulkInsertAsync(documents, mode, batchSize, cancellation);
        }

        public Task BulkInsertDocumentsAsync(IEnumerable<object> documents,
            BulkInsertMode mode = BulkInsertMode.InsertsOnly,
            int batchSize = 1000, CancellationToken cancellation = default)
        {
            var bulkInsertion = new BulkInsertion(Tenancy.Default, Options);
            return bulkInsertion.BulkInsertDocumentsAsync(documents, mode, batchSize, cancellation);
        }

        public Task BulkInsertDocumentsAsync(string tenantId, IEnumerable<object> documents,
            BulkInsertMode mode = BulkInsertMode.InsertsOnly,
            int batchSize = 1000, CancellationToken cancellation = default)
        {
            var bulkInsertion = new BulkInsertion(Tenancy.GetTenant(tenantId), Options);
            return bulkInsertion.BulkInsertDocumentsAsync(documents, mode, batchSize, cancellation);
        }

        public IDiagnostics Diagnostics { get; }

        public IDocumentSession OpenSession(SessionOptions options)
        {
            return openSession(options);
        }

        public IDocumentSession OpenSession(DocumentTracking tracking = DocumentTracking.IdentityOnly,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
        {
            return openSession(new SessionOptions {Tracking = tracking, IsolationLevel = isolationLevel});
        }

        public IDocumentSession OpenSession(string tenantId, DocumentTracking tracking = DocumentTracking.IdentityOnly,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
        {
            return openSession(new SessionOptions
            {
                Tracking = tracking, IsolationLevel = isolationLevel, TenantId = tenantId
            });
        }

        public IDocumentSession DirtyTrackedSession(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
        {
            return OpenSession(DocumentTracking.DirtyTracking, isolationLevel);
        }

        public IDocumentSession DirtyTrackedSession(string tenantId,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
        {
            return OpenSession(tenantId, DocumentTracking.DirtyTracking, isolationLevel);
        }

        public IDocumentSession LightweightSession(IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
        {
            return OpenSession(DocumentTracking.None, isolationLevel);
        }

        public IDocumentSession LightweightSession(string tenantId,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted)
        {
            return OpenSession(tenantId, DocumentTracking.None, isolationLevel);
        }

        public IQuerySession QuerySession(SessionOptions options)
        {
            var connection = options.Initialize(this, CommandRunnerMode.ReadOnly);

            return new QuerySession(this, options, connection);
        }

        public IQuerySession QuerySession()
        {
            return QuerySession(Marten.Storage.Tenancy.DefaultTenantId);
        }

        public IQuerySession QuerySession(string tenantId)
        {
            var options = new SessionOptions
            {
                TenantId = tenantId
            };

            var connection = options.Initialize(this, CommandRunnerMode.ReadOnly);

            var session = new QuerySession(this, options, connection);

            return session;
        }

        public IProjectionDaemon BuildProjectionDaemon(ILogger? logger = null)
        {
            logger ??= new NulloLogger();

            var detector = new HighWaterDetector(new AutoOpenSingleQueryRunner(Tenancy.Default), Events);

            return new ProjectionDaemon(this, detector, logger);
        }

        /// <summary>
        ///     Quick way to stand up a DocumentStore to the given database connection
        ///     in the "development" mode for auto-creating schema objects as needed
        ///     with the default behaviors
        /// </summary>
        /// <param name="connectionString"></param>
        /// <returns></returns>
        public static DocumentStore For(string connectionString)
        {
            return For(_ =>
            {
                _.Connection(connectionString);
            });
        }

        /// <summary>
        ///     Configures a DocumentStore for an existing StoreOptions type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public static DocumentStore For<T>() where T : StoreOptions, new()
        {
            return new(new T());
        }

        /// <summary>
        ///     Configures a DocumentStore by defining the StoreOptions settings first
        /// </summary>
        /// <param name="configure"></param>
        /// <returns></returns>

        #region sample_DocumentStore.For

        public static DocumentStore For(Action<StoreOptions> configure)
        {
            var options = new StoreOptions();
            configure(options);

            return new DocumentStore(options);
        }

        #endregion

        private IDocumentSession openSession(SessionOptions options)
        {
            var connection = options.Initialize(this, CommandRunnerMode.Transactional);

            IDocumentSession session = options.Tracking switch
            {
                DocumentTracking.None => new LightweightSession(this, options, connection),
                DocumentTracking.IdentityOnly => new IdentityMapDocumentSession(this, options, connection),
                DocumentTracking.DirtyTracking => new DirtyCheckingDocumentSession(this, options, connection),
                _ => throw new ArgumentOutOfRangeException(nameof(SessionOptions.Tracking))
            };

            return session;
        }

        public async Task<IDocumentSession> OpenSessionAsync(SessionOptions options, CancellationToken token = default)
        {
            var connection = await options.InitializeAsync(this, CommandRunnerMode.Transactional, token).ConfigureAwait(false);

            IDocumentSession session = options.Tracking switch
            {
                DocumentTracking.None => new LightweightSession(this, options, connection),
                DocumentTracking.IdentityOnly => new IdentityMapDocumentSession(this, options, connection),
                DocumentTracking.DirtyTracking => new DirtyCheckingDocumentSession(this, options, connection),
                _ => throw new ArgumentOutOfRangeException(nameof(SessionOptions.Tracking))
            };

            return session;
        }
    }
}
