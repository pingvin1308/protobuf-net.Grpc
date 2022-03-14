﻿using Grpc.Core;
using Microsoft.Extensions.Logging;
using ProtoBuf.Grpc.Lite.Connections;
using ProtoBuf.Grpc.Lite.Internal;
using ProtoBuf.Grpc.Lite.Internal.Server;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace ProtoBuf.Grpc.Lite
{
    public sealed class LiteServer
    {
        public LiteServer(ILogger? logger = null)
            => Logger = logger;

        internal readonly ILogger? Logger;

        private int id = -1;
        internal int NextId() => Interlocked.Increment(ref id);

        CancellationTokenSource _serverShutdown = new CancellationTokenSource();
        public void Stop() => _serverShutdown.Cancel();
        public Task ListenAsync(Func<CancellationToken, ValueTask<ConnectionState<IFrameConnection>>> listener)
            => Task.Run(() => ListenAsyncCore(listener));

        internal void ListenAsync(Func<CancellationToken, ValueTask<ConnectionState<Stream>>> factory)
            => ListenAsync(factory.WithFrameBuffer(0, 0));

        private async Task ListenAsyncCore(Func<CancellationToken, ValueTask<ConnectionState<IFrameConnection>>> listener)
        {
            try
            {
                while (!_serverShutdown.IsCancellationRequested)
                {
                    Logger.LogInformation(true, static (state, _) => $"[server] listening for new connection...");
                    var connection = await listener(_serverShutdown.Token);
                    if (connection is null)
                    {
                        await Task.Yield(); // at least let's free up the core, if something odd is happening
                        continue;
                    }

                    Logger.LogInformation(connection, static (state, _) => $"[server] established connection {state.Name}");
                    _ = Task.Run(() => RunServer(connection.Connection, connection.Logger));
                }
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == _serverShutdown.Token)
            { } // that's success
        }

        private async Task RunServer(IFrameConnection connection, ILogger? logger)
        {
            try
            {
                connection = connection.WithThreadSafeWrite();
                await using (connection)
                {
                    var server = new LiteServerConnection(this, connection, logger); // this starts processing on another worker
                    await server.RunAsync(_serverShutdown.Token);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }

        }

        private LiteServiceBinder? _serviceBinder;
        public void ManualBind<T>(T? server = null) where T : class
        {
            var binder = typeof(T).GetCustomAttribute<BindServiceMethodAttribute>(true);
            if (binder is null) throw new InvalidOperationException("No " + nameof(BindServiceMethodAttribute) + " found");
            if (binder.BindType is null) throw new InvalidOperationException("No " + nameof(BindServiceMethodAttribute) + "." + nameof(BindServiceMethodAttribute.BindType) + " found");

            var method = binder.BindType.FindMembers(MemberTypes.Method, BindingFlags.Public | BindingFlags.Static,
                static (member, state) =>
                {
                    if (member is not MethodInfo method) return false;
                    if (method.Name != (string)state!) return false;

                    if (method.ReturnType != typeof(void)) return false;
                    var args = method.GetParameters();
                    if (args.Length != 2) return false;
                    if (args[0].ParameterType != typeof(ServiceBinderBase)) return false;
                    if (!args[1].ParameterType.IsAssignableFrom(typeof(T))) return false;
                    return true;

                }, binder.BindMethodName).OfType<MethodInfo>().SingleOrDefault();
            if (method is null) throw new InvalidOperationException("No suitable " + binder.BindType.Name + "." + binder.BindMethodName + " method found");

            server ??= Activator.CreateInstance<T>();
            _serviceBinder ??= new LiteServiceBinder(this);
            method.Invoke(null, new object[] { _serviceBinder, server });
        }

        public int MethodCount => _handlers.Count;

        readonly ConcurrentDictionary<string, Func<IServerHandler>> _handlers = new ConcurrentDictionary<string, Func<Internal.Server.IServerHandler>>();
        internal void AddHandler(string fullName, Func<IServerHandler> handlerFactory)
        {
            if (!_handlers.TryAdd(fullName, handlerFactory)) ThrowDuplicate(fullName);
            static void ThrowDuplicate(string fullName) => throw new ArgumentException($"The method '{fullName}' already exists", nameof(fullName));
        }
        internal bool TryGetHandler(string fullName,
#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP3_1_OR_GREATER
            [MaybeNullWhen(false)]
#endif
            out Func<Internal.Server.IServerHandler> handlerFactory)
            => _handlers.TryGetValue(fullName, out handlerFactory);
    }
}