﻿using System;
using System.ComponentModel.Composition;
using System.Reactive.Linq;
using System.Threading;

using PluginHost.Extensions.Time;
using PluginHost.Interface.Tasks;
using PluginHost.Interface.Logging;

namespace PluginHost.App.Tasks
{
    /// <summary>
    /// The EventLoop class is used to drive scheduled background tasks in the application.
    /// It does this by publishing a Tick event once per second via the EventBus.
    ///
    /// By subscribing to the Tick event, tasks can schedule themselves for execution.
    /// Use subscription throttling to control how often your task is executed.
    ///
    /// The ScheduledTask abstract class already wraps up the behavior for tasks of this type,
    /// simply implement it, and provide the base constructor with the TimeSpan defining the
    /// interval to execute on. See that class for implementation instructions.
    /// </summary>
    /// <seealso cref="ScheduledTask"/>
    public sealed class EventLoop : IEventLoop
    {
        private bool _started      = false;
        private bool _shuttingDown = false;

        private IDisposable _subscription;
        private CancellationToken _cancelToken;
        private ILogger _logger;
        private IEventBus _eventBus;

        public EventLoop(ILogger logger, IEventBus eventBus)
        {
            _logger = logger;
            _eventBus = eventBus;
        }

        /// <summary>
        /// Starts the event loop
        /// </summary>
        public void Start()
        {
            if (_started)
                throw new Exception("Invalid call to EventLoop.Start when already started.");
            _started = true;

            Init();
        }

        /// <summary>
        /// An alternate start method which allows us to proactively stop execution of
        /// the event loop via a CancellationToken.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token to watch</param>
        public void Start(CancellationToken cancellationToken)
        {
            if (_started)
                throw new Exception("Invalid call to EventLoop.Start when already started.");
            _started = true;

            // Enable external cancellation
            _cancelToken = cancellationToken;
            _cancelToken.Register(() => Stop(true));

            Init();
        }

        private void Init()
        {
            // Generate event once per second while enabled
            var ticks = Observable
                .Interval(1.Seconds())
                .DoWhile(IsEnabled);
            // Publish a tick event for each interval
            _subscription = ticks
                .Select(_ => new Tick())
                .Subscribe(_eventBus.Publish);

            _logger.Success("EventLoop has been started.");
        }

        private void HandleError(Exception ex)
        {
            _logger.Error(ex);
        }

        public void Stop(bool immediate)
        {
            if (_shuttingDown)
                return;
            _shuttingDown = true;

            if (_subscription != null)
                _subscription.Dispose();

            _logger.Warn("EventLoop stopped.");
        }

        private bool IsEnabled()
        {
            if (_shuttingDown)
                return false;
            if (_cancelToken != null && _cancelToken.IsCancellationRequested)
                return false;

            return true;
        }
    }
}