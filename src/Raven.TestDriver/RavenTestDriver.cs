﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NETSTANDARD1_5
using System.Runtime.Loader;
#endif
using System.Text;
using System.Threading;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Cluster;
using Raven.Client.Exceptions.Database;
using Raven.Client.Server;
using Raven.Client.Server.Operations;
using Raven.Client.Util;

namespace Raven.TestDriver
{
    public class RavenTestDriver<TServerLocator> : IDisposable
        where TServerLocator : RavenServerLocator, new()
    {
        private static readonly Lazy<IDocumentStore> GlobalServer = 
            new Lazy<IDocumentStore>(RunServer, LazyThreadSafetyMode.ExecutionAndPublication);

        private static Process _globalServerProcess;

        private readonly ConcurrentDictionary<DocumentStore, object> _documentStores = 
            new ConcurrentDictionary<DocumentStore, object>();

        private int _index;

        protected virtual string DatabaseDumpFilePath => null;

        protected virtual Stream DatabaseDumpFileStream => null;

        protected bool IsDisposed { get; private set; }

        public IDocumentStore GetDocumentStore([CallerMemberName] string database = null, TimeSpan? waitForIndexingTimeout = null)
        {
            var name = database + "_" + Interlocked.Increment(ref _index);

            var documentStore = GlobalServer.Value;

            var createDatabaseOperation = new CreateDatabaseOperation(new DatabaseRecord(name));
            documentStore.Admin.Server.Send(createDatabaseOperation);

            var store = new DocumentStore
            {
                Urls = documentStore.Urls,
                Database = name
            };

            store.Initialize();

            store.AfterDispose += (sender, args) =>
            {
                if (_documentStores.TryRemove(store, out _) == false)
                    return;

                try
                {
                    store.Admin.Server.Send(new DeleteDatabaseOperation(store.Database, true));
                }
                catch (DatabaseDoesNotExistException)
                {
                }
                catch (NoLeaderException)
                {
                }
            };

            ImportDatabase(store, database);

            SetupDatabase(store);

            WaitForIndexingInternal(store, name, waitForIndexingTimeout);

            _documentStores[store] = null;

            return store;
        }

        protected virtual void SetupDatabase(IDocumentStore documentStore)
        {
        }

        protected event EventHandler DriverDisposed;

        private void ImportDatabase(DocumentStore docStore, string database)
        {
            var smugglerOpts = new DatabaseSmugglerOptions()
            {
                Database =  database
            };

            if (DatabaseDumpFilePath != null)
            {
                AsyncHelpers.RunSync(() => docStore.Smuggler
                    .ImportAsync(smugglerOpts, DatabaseDumpFilePath));
            }
            else if (DatabaseDumpFileStream != null)
            {
                AsyncHelpers.RunSync(() => docStore.Smuggler
                    .ImportAsync(smugglerOpts, DatabaseDumpFileStream));
            }
        }

        private static IDocumentStore RunServer()
        {
            var process = _globalServerProcess = RavenServerRunner<TServerLocator>.Run(new TServerLocator());

#if NETSTANDARD1_3
            AppDomain.CurrentDomain.ProcessExit += (s, args) =>
            {
                KillGlobalServerProcess();
            };
#endif

#if NETSTANDARD1_5
            AssemblyLoadContext.Default.Unloading += c =>
            {
                KillGlobalServerProcess();
            };
#endif

            string line;
            string url = null;
            var output = process.StandardOutput;
            var sb = new StringBuilder();
            while ((line = output.ReadLine()) != null)
            {
                sb.AppendLine(line);
                const string prefix = "Listening on: ";
                if (line.StartsWith(prefix))
                {
                    url = line.Substring(prefix.Length);
                    break;
                }
            }

            Console.WriteLine(url);

            if (url == null)
            {
                process.Kill();
                throw new InvalidOperationException("Unable to start server, log is: " + Environment.NewLine + sb);
            }

            output.ReadToEndAsync()
                .ContinueWith(x => GC.KeepAlive(x.Exception)); // just discard any other output

            var store = new DocumentStore
            {
                Urls = new[] {url},
                Database = "test.manager"
            };

            return store.Initialize();
        }

        private static void KillGlobalServerProcess()
        {
            var p = _globalServerProcess;
            if (p != null && p.HasExited == false)
                p.Kill();
        }

        public void WaitForIndexing(IDocumentStore store, string database = null, TimeSpan? timeout = null)
        {
            WaitForIndexingInternal(store, database ?? store.Database, timeout ?? TimeSpan.FromMinutes(1));
        }

        private void WaitForIndexingInternal(IDocumentStore store, string database = null, TimeSpan? timeout = null)
        {
            var admin = store.Admin.ForDatabase(database);

            timeout = timeout ?? TimeSpan.Zero;

            var sp = Stopwatch.StartNew();
            while (sp.Elapsed < timeout.Value)
            {
                var databaseStatistics = admin.Send(new GetStatisticsOperation());
                var indexes = databaseStatistics.Indexes
                    .Where(x => x.State != IndexState.Disabled);

                if (indexes.All(x => x.IsStale == false 
                    && x.Name.StartsWith(Constants.Documents.Indexing.SideBySideIndexNamePrefix) == false))
                    return;

                if (databaseStatistics.Indexes.Any(x => x.State == IndexState.Error))
                {
                    break;
                }

                Thread.Sleep(100);
            }

            var errors = admin.Send(new GetIndexErrorsOperation());

            string allIndexErrorsText = string.Empty;
            if (errors != null && errors.Length > 0)
            {
                var allIndexErrorsListText = string.Join("\r\n", 
                    errors.Select(FormatIndexErrors));
                allIndexErrorsText = $"Indexing errors:\r\n{ allIndexErrorsListText }";

                string FormatIndexErrors(IndexErrors indexErrors)
                {
                    var errorsListText = string.Join("\r\n", 
                        indexErrors.Errors.Select(x => $"- {x}"));
                    return $"Index '{indexErrors.Name}' ({indexErrors.Errors.Length} errors):\r\n{errorsListText}";
                }
            }

            throw new TimeoutException($"The indexes stayed stale for more than {timeout.Value}.{ allIndexErrorsText }");
        }

        public void WaitForUserToContinueTheTest(IDocumentStore store)
        {
            var databaseNameEncoded = Uri.EscapeDataString(store.Database);
            var documentsPage = store.Urls[0] + "/studio/index.html#databases/documents?&database=" + databaseNameEncoded + "&withStop=true";

            OpenBrowser(documentsPage); // start the server

            do
            {
                Thread.Sleep(500);

                using (var session = store.OpenSession())
                {
                    if (session.Load<object>("Debug/Done") != null)
                        break;
                }
            } while (true);
        }

        private void OpenBrowser(string url)
        {
            Console.WriteLine(url);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start \"Stop & look at studio\" \"{url}\"")); // Works ok on windows
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url); // Works ok on linux
            }
            else
            {
                Console.WriteLine("Do it yourself!");
            }
        }

        protected void Teardown()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            var exceptions = new List<Exception>();
            var stores = _documentStores.Keys.ToList();
            foreach (var documentStore in stores)
            {
                try
                {
                    documentStore.Dispose();
                }
                catch (Exception e)
                {
                    exceptions.Add(e);
                }
            }

            DatabaseDumpFileStream?.Dispose();

            IsDisposed = true;

            DriverDisposed?.Invoke(this, null);

            if (exceptions.Count > 0)
                throw new AggregateException(exceptions);
        }
    }
}