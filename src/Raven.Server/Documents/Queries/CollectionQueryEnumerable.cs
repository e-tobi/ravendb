﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Raven.Client;
using Raven.Client.Exceptions;
using Raven.Server.Documents.Includes;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Results;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Json;
using Voron;
using Query = Raven.Server.Documents.Queries.AST.Query;

namespace Raven.Server.Documents.Queries
{
    public class CollectionQueryEnumerable : IEnumerable<Document>
    {
        private readonly DocumentDatabase _database;
        private readonly DocumentsStorage _documents;
        private readonly FieldsToFetch _fieldsToFetch;
        private readonly DocumentsOperationContext _context;
        private readonly IncludeDocumentsCommand _includeDocumentsCommand;
        private readonly Reference<int> _totalResults;
        private readonly string _collection;
        private readonly IndexQueryServerSide _query;
        private readonly bool _isAllDocsCollection;

        public CollectionQueryEnumerable(DocumentDatabase database, DocumentsStorage documents, FieldsToFetch fieldsToFetch, string collection,
            IndexQueryServerSide query, DocumentsOperationContext context, IncludeDocumentsCommand includeDocumentsCommand, Reference<int> totalResults)
        {
            _database = database;
            _documents = documents;
            _fieldsToFetch = fieldsToFetch;
            _collection = collection;
            _isAllDocsCollection = collection == Constants.Documents.Collections.AllDocumentsCollection;
            _query = query;
            _context = context;
            _includeDocumentsCommand = includeDocumentsCommand;
            _totalResults = totalResults;
        }

        public IEnumerator<Document> GetEnumerator()
        {
            return new Enumerator(_database, _documents, _fieldsToFetch, _collection, _isAllDocsCollection, _query, _context, _includeDocumentsCommand, _totalResults);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private class Enumerator : IEnumerator<Document>
        {
            private readonly DocumentsStorage _documents;
            private readonly FieldsToFetch _fieldsToFetch;
            private readonly DocumentsOperationContext _context;
            private readonly Reference<int> _totalResults;
            private readonly string _collection;
            private readonly bool _isAllDocsCollection;
            private readonly IndexQueryServerSide _query;

            private bool _initialized;

            private int _returnedResults;

            private readonly HashSet<ulong> _alreadySeenProjections;
            private int _start;
            private IEnumerator<Document> _inner;
            private int _innerCount;
            private readonly List<Slice> _ids;
            private readonly Sort _sort;
            private readonly MapQueryResultRetriever _resultsRetriever;
            private string _startsWith;

            public Enumerator(DocumentDatabase database, DocumentsStorage documents, FieldsToFetch fieldsToFetch, string collection, bool isAllDocsCollection,
                IndexQueryServerSide query, DocumentsOperationContext context, IncludeDocumentsCommand includeDocumentsCommand, Reference<int> totalResults)
            {
                _documents = documents;
                _fieldsToFetch = fieldsToFetch;
                _collection = collection;
                _isAllDocsCollection = isAllDocsCollection;
                _query = query;
                _context = context;
                _totalResults = totalResults;
                _totalResults.Value = 0;

                if (_fieldsToFetch.IsDistinct)
                    _alreadySeenProjections = new HashSet<ulong>();

                _resultsRetriever = new MapQueryResultRetriever(database, query, documents, context, fieldsToFetch, includeDocumentsCommand);
                
                (_ids, _startsWith) = ExtractIdsFromQuery(query,_resultsRetriever);
                
                _sort = ExtractSortFromQuery(query);

            }

            private static Sort ExtractSortFromQuery(IndexQueryServerSide query)
            {
                if (query.Metadata.OrderBy == null)
                    return null;

                Debug.Assert(query.Metadata.OrderBy.Length == 1);

                var randomField = query.Metadata.OrderBy[0];

                Debug.Assert(randomField.OrderingType == OrderByFieldType.Random);

                var customFieldName = randomField.Name;

                if (string.IsNullOrEmpty(customFieldName))
                    return new Sort(null);

                return new Sort(customFieldName);
            }

            private (List<Slice>, string) ExtractIdsFromQuery(IndexQueryServerSide query, MapQueryResultRetriever resultsRetriever)
            {
                if (string.IsNullOrWhiteSpace(query.Query))
                    return (null, null);

                if (query.Metadata.Query.Where == null)
                    return (null, null);

                if (query.Metadata.IndexFieldNames.Contains(QueryFieldName.DocumentId) == false)
                    return (null, null);

                var idsRetriever = new RetrieveDocumentIdsVisitor(resultsRetriever, query.Metadata, _context.Allocator);

                idsRetriever.Visit(query.Metadata.Query.Where, query.QueryParameters);
                
                return (idsRetriever.Ids.OrderBy(x => x, SliceComparer.Instance).ToList() , idsRetriever.StartsWith);
            }

            public bool MoveNext()
            {
                if (_initialized == false)
                    _start = Initialize();

                while (true)
                {
                    if (_inner == null)
                    {
                        _inner = GetDocuments().GetEnumerator();
                        _innerCount = 0;
                    }

                    if (_inner.MoveNext() == false)
                    {
                        Current = null;

                        if (_returnedResults >= _query.PageSize)
                            return false;

                        if (_innerCount < _query.PageSize)
                            return false;

                        _start += _query.PageSize;
                        _inner = null;
                        continue;
                    }

                    if (_resultsRetriever.Filter(_inner.Current, _query.Metadata))
                    {
                        continue;
                    }
                    
                    _innerCount++;

                    var doc = _fieldsToFetch.IsProjection
                        ? _resultsRetriever.GetProjectionFromDocument(_inner.Current, null, 0f, _fieldsToFetch, _context, null)
                        : _inner.Current;

                    if (_query.SkipDuplicateChecking || _fieldsToFetch.IsDistinct == false)
                    {
                        _returnedResults++;
                        Current = doc;
                        return true;
                    }

                    if (_alreadySeenProjections.Add(doc.DataHash))
                    {
                        _returnedResults++;
                        Current = doc;
                        return true;
                    }

                    if (_returnedResults >= _query.PageSize)
                        return false;
                }
            }

            private IEnumerable<Document> GetDocuments()
            {
                IEnumerable<Document> documents;
                if (_startsWith != null)
                {
                    documents = _documents.GetDocumentsStartingWith(_context, _startsWith, null, null, null, _start, _query.PageSize);
                }
                else if (_ids != null && _ids.Count > 0)
                {
                    documents = _documents.GetDocuments(_context, _ids, _start, _query.PageSize, _totalResults);
                }
                else if (_isAllDocsCollection)
                {
                    documents = _documents.GetDocumentsFrom(_context, 0, _start, _query.PageSize);
                    _totalResults.Value = (int)_documents.GetNumberOfDocuments(_context);
                }
                else
                {
                    documents = _documents.GetDocumentsFrom(_context, _collection, 0, _start, _query.PageSize);
                    _totalResults.Value = (int)_documents.GetCollection(_collection, _context).Count;
                }
                return ApplySorting(documents);
            }

            private IEnumerable<Document> ApplySorting(IEnumerable<Document> documents)
            {
                if (_sort == null)
                    return documents;

                return documents
                    .OrderBy(x => _sort.Next());
            }

            private int Initialize()
            {
                _initialized = true;

                if (_query.Start == 0)
                    return 0;

                if (_query.SkipDuplicateChecking)
                    return _query.Start;

                if (_fieldsToFetch.IsDistinct == false)
                    return _query.Start;

                var start = 0;
                while (true)
                {
                    var count = 0;
                    foreach (var document in ApplySorting(_documents.GetDocumentsFrom(_context, _collection, 0, start, _query.PageSize)))
                    {
                        count++;

                        var doc = _fieldsToFetch.IsProjection
                            ? _resultsRetriever.GetProjectionFromDocument(document, null, 0f, _fieldsToFetch, _context, null)
                            : _inner.Current;

                        if (doc.Data.Count <= 0)
                            continue;

                        if (_alreadySeenProjections.Add(doc.DataHash) && _alreadySeenProjections.Count == _query.Start)
                            break;
                    }

                    if (_alreadySeenProjections.Count == _query.Start)
                        break;

                    if (count < _query.PageSize)
                        break;

                    start += count;
                }

                return start;
            }

            public void Reset()
            {
                throw new NotSupportedException();
            }

            public Document Current { get; private set; }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
                if (_ids != null)
                {
                    foreach (var id in _ids)
                    {
                        id.Release(_context.Allocator);
                    }
                }
            }

            private class Sort
            {
                private readonly Random _random;

                public Sort(string field)
                {
                    _random = field == null ?
                        new Random() :
                        new Random(field.GetHashCode());
                }

                public int Next()
                {
                    return _random.Next();
                }
            }

            private class RetrieveDocumentIdsVisitor : WhereExpressionVisitor
            {
                private readonly Query _query;
                private readonly MapQueryResultRetriever _resultsRetriever;
                private readonly QueryMetadata _metadata;
                private readonly ByteStringContext _allocator;
                public string StartsWith;

                public readonly List<Slice> Ids = new List<Slice>();

                public RetrieveDocumentIdsVisitor(MapQueryResultRetriever resultsRetriever, QueryMetadata metadata, ByteStringContext allocator) : base(metadata.Query.QueryText)
                {
                    _query = metadata.Query;
                    _resultsRetriever = resultsRetriever;
                    _metadata = metadata;
                    _allocator = allocator;
                }

                public override void VisitBooleanMethod(QueryExpression leftSide, QueryExpression rightSide, OperatorType operatorType, BlittableJsonReaderObject parameters)
                {
                    VisitFieldToken(leftSide, rightSide, parameters);
                }

                public override void VisitFieldToken(QueryExpression fieldName, QueryExpression value, BlittableJsonReaderObject parameters)
                {
                    if (fieldName is MethodExpression me)
                    {
                        var methodType = QueryMethod.GetMethodType(me.Name);
                        switch (methodType)
                        {
                            case MethodType.Id:
                                if (value is ValueExpression ve)
                                {
                                    var id = QueryBuilder.GetValue(_query, _metadata, parameters, ve);

                                    Debug.Assert(id.Type == ValueTokenType.String);

                                    AddId(id.Value.ToString());
                                }
                                else if (_metadata.FillIds != null)
                                {
                                    AddId(_metadata.FillIds.EvaluateSingleMethod(_resultsRetriever, null).ToString());
                                }
                                break;
                        }
                    }
                }

                public override void VisitBetween(QueryExpression fieldName, QueryExpression firstValue, QueryExpression secondValue, BlittableJsonReaderObject parameters)
                {
                    if (fieldName is MethodExpression me && me.Name.Equals("id") && firstValue is ValueExpression fv && secondValue is ValueExpression sv)
                    {
                        throw new InvalidQueryException("Collection query does not support filtering by id() using Between operator. Supported operators are: =, IN",
                            QueryText, parameters);
                    }
                }

                public override void VisitIn(QueryExpression fieldName, List<QueryExpression> values, BlittableJsonReaderObject parameters)
                {
                    if (fieldName is MethodExpression me && me.Name.Equals("id"))
                    {
                        foreach (var item in values)
                        {
                            if (item is ValueExpression iv)
                            {
                                foreach (var id in QueryBuilder.GetValues(_query, _metadata, parameters, iv))
                                {
                                    AddId(id.Value?.ToString());
                                }
                            }
                        }
                    }
                }

                public override void VisitMethodTokens(StringSegment name, List<QueryExpression> arguments, BlittableJsonReaderObject parameters)
                {
                    var expression = arguments[arguments.Count - 1];
                    if (expression is BinaryExpression be && be.Operator == OperatorType.Equal)
                    {
                        VisitFieldToken(new MethodExpression("id", new List<QueryExpression>()), be.Right, parameters);                        
                    }
                    else if (expression is InExpression ie)
                    {
                        VisitIn(new MethodExpression("id", new List<QueryExpression>()), ie.Values, parameters);
                    }
                    else if (string.Equals(name,"startsWith", StringComparison.OrdinalIgnoreCase))
                    {
                        if (expression is ValueExpression iv)
                        {
                            var prefix = QueryBuilder.GetValue(_query, _metadata, parameters, iv);
                            StartsWith = prefix.Value?.ToString();
                        }
                    }
                    else
                    {
                        ThrowNotSupportedCollectionQueryOperator(expression.Type.ToString(), parameters);
                    }
                }

                private void AddId(string id)
                {
                    Slice.From(_allocator, id, out Slice key);
                    _allocator.ToLowerCase(ref key.Content);

                    Ids.Add(key);
                }

                private void ThrowNotSupportedCollectionQueryOperator(string @operator, BlittableJsonReaderObject parameters)
                {
                    throw new InvalidQueryException(
                        $"Collection query does not support filtering by {Constants.Documents.Indexing.Fields.DocumentIdFieldName} using {@operator} operator. Supported operators are: =, IN",
                        QueryText, parameters);
                }
            }
        }
    }
}