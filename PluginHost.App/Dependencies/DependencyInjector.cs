﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Registration;

using PluginHost.App.Tasks;
using PluginHost.App.Helpers;
using PluginHost.App.Configuration;
using PluginHost.Extensions.Functional;
using PluginHost.Interface.Shell;
using PluginHost.Interface.Tasks;
using PluginHost.Interface.Logging;

namespace PluginHost.App.Dependencies
{
    public class DependencyInjector : IDisposable
    {
        private readonly CompositionContainer _container;
        private readonly RegistrationBuilder _conventions;
        private readonly DirectoryCatalog _directoryCatalog;
        private readonly AssemblyCatalog _assemblyCatalog;
        private readonly DirectoryWatcher _watcher;
        private readonly Task _watcherTask;
        private readonly IDisposable _subscription;

        private static DependencyInjector _current;
        public static DependencyInjector Current
        {
            get { return _current ?? (_current = new DependencyInjector()); }
        }

        public Subject<ExportChangedEventArgs> ExportChanged { get; private set; }

        private DependencyInjector()
        {
            ExportChanged = new Subject<ExportChangedEventArgs>();
            _conventions  = GetConventions();

            var assembly  = Assembly.GetExecutingAssembly();
            var directory = Config.Current.Paths.Plugins.Info.FullName;
            var filter    = @"*.dll";

            // Create catalogs
            _directoryCatalog = new DirectoryCatalog(directory, filter, _conventions);
            _assemblyCatalog  = new AssemblyCatalog(assembly, _conventions);

            // Initialize container
            _container = InitContainer();
            _container.ExportsChanged += OnExportsChanged;

            // Start file watcher for the directory catalog
            // so that dependencies can be loaded at runtime
            var logger = Resolve<ILogger>();
            _watcher = new DirectoryWatcher(logger, directory, filter);
            _subscription = _watcher.Events
                .Where(e => e.Type == FileChangedEvent.Ready || e.Type == FileChangedEvent.Removed)
                .ObserveOn(new EventLoopScheduler())
                .Subscribe(OnDependencyChanged);
            _watcherTask = _watcher.Watch();
        }

        public void Inject<T>(T instance)
        {
            _container.ComposeParts(instance);
        }

        public Lazy<T> LazyResolve<T>()
        {
            return _container.GetExport<T>();
        }

        public Lazy<T> LazyResolve<T, TMetadata>(Func<TMetadata, bool> predicate)
        {
            return _container
                .GetExports<T, TMetadata>()
                .SingleOrDefault(export => predicate(export.Metadata));
        }

        public T Resolve<T>()
        {
            return LazyResolve<T>()
                .ToOption()
                .Select(export => export.Value)
                .GetValueOrDefault();
        }

        public T Resolve<T, TMetadata>(Func<TMetadata, bool> predicate)
        {
            return LazyResolve<T, TMetadata>(predicate)
                .ToOption()
                .Select(export => export.Value)
                .GetValueOrDefault();
        }

        public IEnumerable<Lazy<T>> LazyResolveMany<T>()
        {
            return _container.GetExports<T>();
        }

        public IEnumerable<Lazy<T, TMetadata>> LazyResolveMany<T, TMetadata>()
        {
            return _container.GetExports<T, TMetadata>();
        }

        public IEnumerable<Lazy<T, TMetadata>> LazyResolveMany<T, TMetadata>(Func<TMetadata, bool> predicate)
        {
            return _container.GetExports<T, TMetadata>().Where(export => predicate(export.Metadata));
        }

        public IEnumerable<T> ResolveMany<T>()
        {
            return LazyResolveMany<T>().Select(x => x.Value);
        }

        public IEnumerable<T> ResolveMany<T, TMetadata>()
        {
            return LazyResolveMany<T, TMetadata>().Select(x => x.Value);
        }

        public IEnumerable<T> ResolveMany<T, TMetadata>(Func<TMetadata, bool> predicate)
        {
            return LazyResolveMany<T, TMetadata>(predicate).Select(x => x.Value);
        }

        public void Dispose()
        {
            if (_subscription != null)
                _subscription.Dispose();
            if (_watcher != null)
                _watcher.Dispose();
            if (_container != null)
                _container.Dispose();
        }

        private CompositionContainer InitContainer()
        {
            return new CompositionContainer(
                new AggregateCatalog(_assemblyCatalog, _directoryCatalog),
                CompositionOptions.IsThreadSafe
            );
        }

        private RegistrationBuilder GetConventions()
        {
            // When selecting constructors for injection, use the one
            // with the largest number of parameters
            Func<ConstructorInfo[], ConstructorInfo> largestCtor = (info) =>
                info.OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();

            // Ensure that all constructor imports allow recomposition
            Action<ParameterInfo, ImportBuilder> defaultImports = (pi, b) =>
                b.AllowRecomposition();

            var builder = new RegistrationBuilder();
            builder.ForType<Config>()
                .SetCreationPolicy(CreationPolicy.Shared)
                .Export<IConfig>()
                .SelectConstructor(largestCtor, defaultImports);
            builder.ForTypesDerivedFrom<ILogger>()
                .SetCreationPolicy(CreationPolicy.Shared)
                .Export<ILogger>()
                .SelectConstructor(largestCtor, defaultImports);
            builder.ForTypesDerivedFrom<IEventBus>()
                .SetCreationPolicy(CreationPolicy.Shared)
                .Export<IEventBus>()
                .SelectConstructor(largestCtor, defaultImports);
            builder.ForTypesDerivedFrom<IEventLoop>()
                .SetCreationPolicy(CreationPolicy.Shared)
                .Export<IEventLoop>()
                .SelectConstructor(largestCtor, defaultImports);
            builder.ForType<TaskManager>()
                .SetCreationPolicy(CreationPolicy.Shared)
                .SelectConstructor(largestCtor, defaultImports)
                .Export<ITaskManager>();
            builder.ForTypesDerivedFrom<ITask>()
                .Export<ITask>(b => b.AddMetadata(TaskMetadata.MetadataKey, t => t.Name))
                .SelectConstructor(largestCtor, defaultImports);
            builder.ForTypesDerivedFrom<IShellCommand>()
                .Export<IShellCommand>()
                .SelectConstructor(largestCtor, defaultImports);

            return builder;
        }

        private void OnDependencyChanged(FileChangedEventArgs e)
        {
            _directoryCatalog.Refresh();
        }

        private void OnExportsChanged(object sender, ExportsChangeEventArgs e)
        {
            foreach (var removed in e.RemovedExports)
            {
                ExportChanged.OnNext(ExportChangedEventArgs.Removed(removed));
            }
            foreach (var added in e.AddedExports)
            {
                ExportChanged.OnNext(ExportChangedEventArgs.Added(added));
            }
        }
    }
}
