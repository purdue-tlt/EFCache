﻿// Copyright (c) Pawel Kadluczka, Inc. All rights reserved. See License.txt in the project root for license information.

namespace EFCache
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.Common;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;

    public class CachingCommand : DbCommand, ICloneable
    {
        protected readonly DbCommand _command;
        protected readonly CommandTreeFacts _commandTreeFacts;
        protected readonly CacheTransactionHandler _cacheTransactionHandler;
        protected readonly CachingPolicy _cachingPolicy;

        public CachingCommand(DbCommand command, CommandTreeFacts commandTreeFacts, CacheTransactionHandler cacheTransactionHandler, CachingPolicy cachingPolicy)
        {
            Debug.Assert(command != null, "command is null");
            Debug.Assert(commandTreeFacts != null, "commandTreeFacts is null");
            Debug.Assert(cacheTransactionHandler != null, "cacheTransactionHandler is null");
            Debug.Assert(cachingPolicy != null, "cachingPolicy is null");

            _command = command;
            _commandTreeFacts = commandTreeFacts;
            _cacheTransactionHandler = cacheTransactionHandler;
            _cachingPolicy = cachingPolicy;
        }

        public CommandTreeFacts CommandTreeFacts
        {
            get { return _commandTreeFacts; }
        }

        public CacheTransactionHandler CacheTransactionHandler
        {
            get { return _cacheTransactionHandler; }
        }

        internal CachingPolicy CachingPolicy
        {
            get { return _cachingPolicy; }
        }

        internal DbCommand WrappedCommand
        {
            get { return _command; }
        }

        protected bool IsCacheable
        {
            get
            {
                return _commandTreeFacts.IsQuery &&
                       (IsQueryAlwaysCached ||
                       !_commandTreeFacts.UsesNonDeterministicFunctions &&
                       !IsQueryBlacklisted &&
                       _cachingPolicy.CanBeCached(_commandTreeFacts.AffectedEntitySets, CommandText,
                           Parameters.Cast<DbParameter>()
                               .Select(p => new KeyValuePair<string, object>(p.ParameterName, p.Value))));
            }
        }

        private bool IsQueryBlacklisted
        {
            get
            {
                return BlacklistedQueriesRegistrar.Instance.IsQueryBlacklisted(
                    _commandTreeFacts.MetadataWorkspace, CommandText);
            }
        }

        protected bool IsQueryAlwaysCached
        {
            get
            {
                return AlwaysCachedQueriesRegistrar.Instance.IsQueryCached(
                    _commandTreeFacts.MetadataWorkspace, CommandText);
            }
        }

        public override void Cancel()
        {
            _command.Cancel();
        }

        public override string CommandText
        {
            get
            {
                return _command.CommandText;
            }
            set
            {
                _command.CommandText = value;
            }
        }

        public override int CommandTimeout
        {
            get
            {
                return _command.CommandTimeout;
            }
            set
            {
                _command.CommandTimeout = value;
            }
        }

        public override CommandType CommandType
        {
            get
            {
                return _command.CommandType;
            }
            set
            {
                _command.CommandType = value;
            }
        }

        protected override DbParameter CreateDbParameter()
        {
            return _command.CreateParameter();
        }

        protected override DbConnection DbConnection
        {
            get
            {
                return _command.Connection;
            }
            set
            {
                _command.Connection = value;
            }
        }

        protected override DbParameterCollection DbParameterCollection
        {
            get { return _command.Parameters; }
        }

        protected override DbTransaction DbTransaction
        {
            get
            {
                return _command.Transaction;
            }
            set
            {
                _command.Transaction = value;
            }
        }

        public override bool DesignTimeVisible
        {
            get
            {
                return _command.DesignTimeVisible;
            }
            set
            {
                _command.DesignTimeVisible = value;
            }
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            object cacheLock = null;
			var affectedEntitySets = _commandTreeFacts.AffectedEntitySets.Select(s => s.Name).ToList();
            if (!IsCacheable)
            {
                if (!_commandTreeFacts.IsQuery)
                {
                    cacheLock = _cacheTransactionHandler.Lock(affectedEntitySets);
                    _cacheTransactionHandler.InvalidateSets(Transaction, _commandTreeFacts.AffectedEntitySets.Select(s => s.Name),
                        DbConnection);
                }

                var result = _command.ExecuteReader(behavior);

                if (cacheLock != null)
                    _cacheTransactionHandler.ReleaseLock(cacheLock);

                return result;
            }

            var key = CreateKey();

            object value;
            cacheLock = _cacheTransactionHandler.Lock(affectedEntitySets);
            if (_cacheTransactionHandler.GetItem(Transaction, key, DbConnection, out value))
            {
                _cacheTransactionHandler.ReleaseLock(cacheLock);
                return new CachingReader((CachedResults)value);
            }

            using (var reader = _command.ExecuteReader(behavior))
            {
                var queryResults = new List<object[]>();

                while (reader.Read())
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    queryResults.Add(values);
                }

                _cacheTransactionHandler.ReleaseLock(cacheLock);
                return HandleCaching(reader, key, queryResults);
            }
        }

#if !NET40

        protected async override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            object cacheLock = null;
			var affectedEntitySets = _commandTreeFacts.AffectedEntitySets.Select(s => s.Name).ToList();
			if (!IsCacheable)
            {
                if (!_commandTreeFacts.IsQuery)
                {
                    cacheLock = _cacheTransactionHandler.Lock(affectedEntitySets);
                    _cacheTransactionHandler.InvalidateSets(Transaction, affectedEntitySets, DbConnection);
                }

                var result = await _command.ExecuteReaderAsync(behavior, cancellationToken);

                if (cacheLock != null)
                    _cacheTransactionHandler.ReleaseLock(cacheLock);

                return result;
            }

            var key = CreateKey();

            object value;
            cacheLock = _cacheTransactionHandler.Lock(affectedEntitySets);
            if (_cacheTransactionHandler.GetItem(Transaction, key, DbConnection, out value))
            {
                _cacheTransactionHandler.ReleaseLock(cacheLock);
                return new CachingReader((CachedResults)value);
            }

            using (var reader = await _command.ExecuteReaderAsync(behavior, cancellationToken))
            {
                var queryResults = new List<object[]>();

                while (await reader.ReadAsync(cancellationToken))
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    queryResults.Add(values);
                }

                _cacheTransactionHandler.ReleaseLock(cacheLock);
                return HandleCaching(reader, key, queryResults);
            }
        }
#endif

        protected virtual DbDataReader HandleCaching(DbDataReader reader, string key, List<object[]> queryResults)
        {
            var cachedResults =
                new CachedResults(
                    GetTableMetadata(reader), queryResults, reader.RecordsAffected);

            int minCacheableRows, maxCachableRows;
            _cachingPolicy.GetCacheableRows(_commandTreeFacts.AffectedEntitySets, out minCacheableRows,
                out maxCachableRows);

            if (IsQueryAlwaysCached || (queryResults.Count >= minCacheableRows && queryResults.Count <= maxCachableRows))
            {
                TimeSpan slidingExpiration;
                DateTimeOffset absoluteExpiration;
                _cachingPolicy.GetExpirationTimeout(_commandTreeFacts.AffectedEntitySets, out slidingExpiration,
                    out absoluteExpiration);

                _cacheTransactionHandler.PutItem(
                    Transaction,
                    key,
                    cachedResults,
                    _commandTreeFacts.AffectedEntitySets.Select(s => s.Name),
                    slidingExpiration,
                    absoluteExpiration,
                    DbConnection);
            }

            return new CachingReader(cachedResults);
        }

        protected override void Dispose(bool disposing)
        {
            _command.GetType()
                .GetMethod("Dispose", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_command, new object[] { disposing });
        }

        protected static ColumnMetadata[] GetTableMetadata(DbDataReader reader)
        {
            var columnMetadata = new ColumnMetadata[reader.FieldCount];

            for (var i = 0; i < reader.FieldCount; i++)
            {
                columnMetadata[i] =
                    new ColumnMetadata(
                        reader.GetName(i), reader.GetDataTypeName(i), reader.GetFieldType(i));
            }

            return columnMetadata;
        }

        public override int ExecuteNonQuery()
        {
            var recordsAffected = _command.ExecuteNonQuery();

            InvalidateSetsForNonQuery(recordsAffected);

            return recordsAffected;
        }

#if !NET40
        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            var recordsAffected = await _command.ExecuteNonQueryAsync(cancellationToken);

            InvalidateSetsForNonQuery(recordsAffected);

            return recordsAffected;
        }
#endif

        protected virtual void InvalidateSetsForNonQuery(int recordsAffected)
        {
            if (recordsAffected > 0 && _commandTreeFacts.AffectedEntitySets.Any())
            {
                _cacheTransactionHandler.InvalidateSets(Transaction, _commandTreeFacts.AffectedEntitySets.Select(s => s.Name),
                    DbConnection);
            }
        }

        public override object ExecuteScalar()
        {
            if (!IsCacheable)
            {
                return _command.ExecuteScalar();
            }

            var key = CreateKey();

            object value;

            if (_cacheTransactionHandler.GetItem(Transaction, key, DbConnection, out value))
            {
                return value;
            }

            value = _command.ExecuteScalar();

            TimeSpan slidingExpiration;
            DateTimeOffset absoluteExpiration;
            _cachingPolicy.GetExpirationTimeout(_commandTreeFacts.AffectedEntitySets, out slidingExpiration, out absoluteExpiration);

            _cacheTransactionHandler.PutItem(
                Transaction,
                key,
                value,
                _commandTreeFacts.AffectedEntitySets.Select(s => s.Name),
                slidingExpiration,
                absoluteExpiration,
                DbConnection);

            return value;
        }

#if !NET40
        public async override Task<object> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            if (!IsCacheable)
            {
                return await _command.ExecuteScalarAsync(cancellationToken);
            }

            var key = CreateKey();

            object value;

            if (_cacheTransactionHandler.GetItem(Transaction, key, DbConnection, out value))
            {
                return value;
            }

            value = await _command.ExecuteScalarAsync(cancellationToken);

            TimeSpan slidingExpiration;
            DateTimeOffset absoluteExpiration;
            _cachingPolicy.GetExpirationTimeout(_commandTreeFacts.AffectedEntitySets, out slidingExpiration, out absoluteExpiration);

            _cacheTransactionHandler.PutItem(
                Transaction,
                key,
                value,
                _commandTreeFacts.AffectedEntitySets.Select(s => s.Name),
                slidingExpiration,
                absoluteExpiration,
                DbConnection);

            return value;
        }
#endif

        public override void Prepare()
        {
            _command.Prepare();
        }

        public override UpdateRowSource UpdatedRowSource
        {
            get
            {
                return _command.UpdatedRowSource;
            }
            set
            {
                _command.UpdatedRowSource = value;
            }
        }

        protected string CreateKey()
        {
            return
                string.Format(
                "{0}_{1}_{2}",
                Connection.Database,
                CommandText,
                string.Join(
                    "_",
                    Parameters.Cast<DbParameter>()
                    .Select(p => string.Format("{0}={1}", p.ParameterName, p.Value))));
        }

        public object Clone()
        {
            var cloneableCommand = _command as ICloneable;
            if (cloneableCommand == null)
            {
                throw new InvalidOperationException("The underlying DbCommand does not implement the ICloneable interface.");
            }

            var clonedCommand = (DbCommand)cloneableCommand.Clone();
            return new CachingCommand(clonedCommand, _commandTreeFacts, _cacheTransactionHandler, _cachingPolicy);
        }
    }
}