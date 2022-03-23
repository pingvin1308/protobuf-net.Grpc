﻿using Grpc.Core;
using Microsoft.Extensions.Logging;
using ProtoBuf.Grpc.Lite;
using ProtoBuf.Grpc.Lite.Internal;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace protobuf_net.GrpcLite.Test;

[SetLoggingSource]
public class EndToEndTests : IClassFixture<TestServerHost>
{
    private readonly TestServerHost Server;
    private string Name => Server.Name;
    private ILogger Logger { get; }
    public EndToEndTests(TestServerHost server, ITestOutputHelper output)
    {
        Server = server;
        _output = output;
        Logger = _output.CreateLogger("");
    }

    private readonly ITestOutputHelper _output;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    CancellationTokenSource After() => After(DefaultTimeout);

    CancellationTokenSource After(TimeSpan timeout)
    {
        var cts = new CancellationTokenSource();
        //if (!Debugger.IsAttached)
        cts.CancelAfter(timeout);
        return cts;
    }

    private LogCapture? ServerLog() => Server.WithLog(_output);

    ValueTask<LiteChannel> ConnectAsync(CancellationToken cancellationToken)
        => ConnectionFactory.ConnectNamedPipe(Name, logger: Logger).CreateChannelAsync(cancellationToken);

    [Fact]
    public async Task CanCallUnarySync()
    {
        using var log = ServerLog();
        using var timeout = After();
        await using var client = await ConnectAsync(timeout.Token);
        var proxy = new FooService.FooServiceClient(client);

        Logger.Information($"issuing {nameof(proxy.Unary)}...");
        var response = proxy.Unary(new FooRequest { Value = 42 }, default(CallOptions).WithCancellationToken(timeout.Token));
        Logger.Information($"got response: {response}");

        Assert.NotNull(response);
        Assert.Equal(42, response.Value);
        timeout.Cancel();
    }

    [Fact]
    public async Task CanCallUnaryAsync()
    {
        using var log = ServerLog();
        using var timeout = After();
        await using var client = await ConnectionFactory.ConnectNamedPipe(Name, logger: Logger).CreateChannelAsync(timeout.Token);

        var proxy = new FooService.FooServiceClient(client);

        Logger.Information($"issuing {nameof(proxy.UnaryAsync)}...");
        using var call = proxy.UnaryAsync(new FooRequest { Value = 42 }, default(CallOptions).WithCancellationToken(timeout.Token));
        Logger.Information("awaiting response...");
        var response = await call.ResponseAsync;
        Logger.Information($"got response: {response}");

        Assert.NotNull(response);
        Assert.Equal(42, response.Value);
        timeout.Cancel();
    }

    [Fact]
    public async Task CanCallNullDuplexAsync()
    {
        using var log = ServerLog();
        using var timeout = After();
        await using var client = this.Server.ConnectLocal();

        await RunDuplex(client, timeout.Token);

        timeout.Cancel();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    public async Task LongNullUnaryAsync(int count, int timeoutSeconds = 10)
    {
        using var log = ServerLog();
        using var timeout = After(TimeSpan.FromSeconds(timeoutSeconds));
        await using var client = this.Server.ConnectLocal();

        var proxy = new FooService.FooServiceClient(client);
        var options = new CallOptions(cancellationToken: timeout.Token);
        for (int i = 0; i < count; i++)
        {
            var result = await proxy.UnaryAsync(new FooRequest { Value = i }, options);
            Assert.Equal(i, result.Value);
        }
        await RunDuplex(client, timeout.Token);

        timeout.Cancel();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    public async Task LongClientStreamingAsync(int count, int timeoutSeconds = 10)
    {
        using var log = ServerLog();
        using var timeout = After(TimeSpan.FromSeconds(timeoutSeconds));
        await using var client = this.Server.ConnectLocal();

        var proxy = new FooService.FooServiceClient(client);
        var options = new CallOptions(cancellationToken: timeout.Token);

        using var call = proxy.ClientStreaming(options);
        int sum = 0;
        for (int i = 0; i < count; i++)
        {
            await call.RequestStream.WriteAsync(new FooRequest { Value = i });
            sum += i;
        }
        await call.RequestStream.CompleteAsync();
        var resp = await call.ResponseAsync;
        Assert.Equal(sum, resp.Value);
    }

    [Fact]
    public async Task CanCallDuplexAsync()
    {
        using var log = ServerLog();
        using var timeout = After();
        Debug.WriteLine($"[client] connecting {Name}...");
        await using var client = await ConnectionFactory.ConnectNamedPipe(Name, logger: Logger).CreateChannelAsync(timeout.Token);

        await RunDuplex(client, timeout.Token);

        timeout.Cancel();
    }

    private async Task RunDuplex(LiteChannel client, CancellationToken cancellationToken)
    {
        var proxy = new FooService.FooServiceClient(client);

        using var call = proxy.Duplex();
        for (int i = 0; i < 10; i++)
        {
            Logger.Information($"writing {i}...");
            await call.RequestStream.WriteAsync(new FooRequest { Value = i });
            // await pipe.RequestStream.CompleteAsync();
        }
        Logger.Information($"all writes complete");
        for (int i = 0; i < 10; i++)
        {
            Logger.Information($"reading {i}...");
            Assert.True(await call.ResponseStream.MoveNext(cancellationToken));
            Assert.Equal(i, call.ResponseStream.Current.Value);
        }
        Logger.Information($"all reads complete");
    }
}

public sealed class LogCapture : IDisposable
{
    private readonly TestServerHost host;
    private readonly ITestOutputHelper output;

    public LogCapture(TestServerHost host, ITestOutputHelper output)
    {
        this.host = host;
        this.output = output;
        host.Log += OnLog;
    }

    private void OnLog(string message) => output?.WriteLine(message);

    public void Dispose() => host.Log -= OnLog;
}

public class TestServerHost : IDisposable, ILogger
{
    public event Action<string>? Log;
    public LogCapture? WithLog(ITestOutputHelper output)
        => output is null ? default : new LogCapture(this, output);

    private readonly LiteServer _server;
    public string Name { get; }

    public LiteChannel ConnectLocal() => _server.CreateLocalClient();

    public TestServerHost()
    {

        Name = Guid.NewGuid().ToString();
        _server = new LiteServer(logger: this);
        var svc = new MyService();
        svc.Log += message => this.Information(message);
        _server.Bind<MyService>(svc);

        Debug.WriteLine($"starting listener {Name}...");
        _server.ListenAsync(ConnectionFactory.ListenNamedPipe(Name, logger: this));
    }

    public void Dispose() => _server.Stop();

    void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Log?.Invoke(BasicLogger.Format(logLevel, eventId, state, exception, formatter, ""));

    bool ILogger.IsEnabled(LogLevel logLevel) => Log is not null;

    IDisposable ILogger.BeginScope<TState>(TState state) => null!;
}

public class MyService : FooService.FooServiceBase
{
    public event Action<string>? Log;
    
    private void OnLog(string message) => Log?.Invoke(message);
    public override async Task<FooResponse> Unary(FooRequest request, ServerCallContext context)
    {
        OnLog($"unary starting; received {request.Value}");
        await Task.Yield();
        OnLog("unary returning");
        return new FooResponse { Value = request.Value };
    }

    public override async Task Duplex(IAsyncStreamReader<FooRequest> requestStream, IServerStreamWriter<FooResponse> responseStream, ServerCallContext context)
    {
        OnLog("duplex starting");
        while (await requestStream.MoveNext(context.CancellationToken))
        {
            var value = requestStream.Current;
            OnLog($"duplex received {value.Value}");
            await responseStream.WriteAsync(new FooResponse {  Value = value.Value });
        }
        OnLog("duplex returning");
    }

    public override async Task ServerStreaming(FooRequest request, IServerStreamWriter<FooResponse> responseStream, ServerCallContext context)
    {
        var count = request.Value;
        for (int i = 0; i < count; i++)
        {
            await responseStream.WriteAsync(new FooResponse { Value = i });
        }
    }
    public override async Task<FooResponse> ClientStreaming(IAsyncStreamReader<FooRequest> requestStream, ServerCallContext context)
    {
        OnLog("client-streaming starting");
        int sum = 0;
        while (await requestStream.MoveNext(context.CancellationToken))
        {
            var value = requestStream.Current;
            OnLog($"client-streaming received {value.Value}");
            sum += value.Value;
        }
        OnLog("client-streaming returning");
        return new FooResponse { Value = sum };
    }
}
