﻿using Grpc.Core;
using Grpc.Net.Client;
using ProtoBuf.Grpc.Lite;
using protobuf_net.GrpcLite.Test;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using static FooService;

static class Program
{
    static readonly Dictionary<string, (string unarySequential, string unaryConcurrent, string clientStreamingBuffered, string clientStreamingNonBuffered, string serverStreamingBuffered, string serverStreamingNonBuffered, string duplex)> timings = new();

    [Flags]
    public enum Tests
    {
        None = 0,
        NamedPipe = 1 << 0,
        NamedPipeTls = 1 << 1,
        NamedPipePassThru = 1 << 2,
        NamedPipeMerge = 1 << 3,
        Tcp = 1 << 4,
        TcpTls = 1 << 5,
        TcpKestrel = 1 << 6,
        Unmanaged = 1 << 7,
        Local = 1 << 8,
        Managed = 1 << 9,
        ManagedTls = 1 << 10,
        TcpSAEA = 1 << 11,
        TcpTlsClientCert = 1 << 12,
    }
    static async Task<int> Main(string[] args)
    {
        try
        {
            var userCert = new X509Certificate2("fred.pfx", "password");
            RemoteCertificateValidationCallback trustAny = (object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
                =>
            {
                Console.WriteLine($"Received cert from server '{certificate?.Subject}'; {sslPolicyErrors}; trusting...");
                return true;
            };
            LocalCertificateSelectionCallback selectCert = (object sender, string targetHost, X509CertificateCollection localCertificates, X509Certificate? remoteCertificate, string[] acceptableIssuers)
                =>
            {
                Console.WriteLine($"Being challenged for cert from server '{remoteCertificate?.Subject}' for {targetHost}; providing {userCert?.Subject}...");
                return userCert!;
            };
            Tests tests;
            if (args.Length == 0)
            {
                // reasonable defaults
                tests = Tests.NamedPipe | Tests.Local | Tests.Tcp | Tests.Unmanaged | Tests.TcpTls | Tests.TcpTlsClientCert | Tests.NamedPipeTls
                    | Tests.ManagedTls;
#if NET472
                tests |= Tests.TcpSAEA; // something glitching here on net6; probably fixable
#else
                tests |= Tests.Managed; // net472 doesn't like non-TLS gRPC, even with the feature-flag set
#endif
            }
            else
            {
                tests = Tests.None;
                foreach (var arg in args)
                {
                    if (Enum.TryParse(arg, true, out Tests tmp))
                        tests |= tmp;
                }
            }
            if (tests == Tests.None)
            {
                Console.WriteLine("No tests selected");
                foreach (Tests test in Enum.GetValues(typeof(Tests)))
                {
                    if (test != Tests.None)
                    {
                        Console.WriteLine($"\t{test}");
                    }
                }
                return -1;
            }
            Console.WriteLine($"Running tests: {tests}");

            async Task Execute<T>(Tests test, Func<ValueTask<T>> channelCreator, int repeatCount = 5, bool runClientStreaming = true, Func<T, ValueTask>? after = null) where T : ChannelBase
            {
                T? channel = null;
                try
                {
                    if ((tests & test) != 0)
                    {
                        channel = await channelCreator();
                        await Run(channel, test, repeatCount, runClientStreaming);
                        if (after is not null)
                        {
                            try { await after(channel); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                }
                finally
                {
                    try
                    {
                        switch (channel)
                        {
                            case IAsyncDisposable ad:
                                await ad.DisposeAsync();
                                break;
                            case IDisposable d:
                                d.Dispose();
                                break;
                        }
                    }
                    catch { }
                }
            }
            GrpcChannelOptions grpcChannelOptions = new();
#if NET472
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
            grpcChannelOptions.HttpHandler = new WinHttpHandler();
            //const bool ManagedClientStreaming = false;
#else
            //const bool ManagedClientStreaming = true;
#endif
            const bool ManagedClientStreaming = true; // always try, even if we think it is doomed

            await Execute(Tests.TcpKestrel, () => ConnectionFactory.ConnectSocket(new IPEndPoint(IPAddress.Loopback, 10044)).AsStream().AsFrames().CreateChannelAsync(TimeSpan.FromSeconds(5)));
            await Execute(Tests.NamedPipe, () => ConnectionFactory.ConnectNamedPipe("grpctest_buffer").AsFrames().CreateChannelAsync(TimeSpan.FromSeconds(5)));
            await Execute(Tests.NamedPipePassThru, () => ConnectionFactory.ConnectNamedPipe("grpctest_passthru").AsFrames(outputBufferSize: 0).CreateChannelAsync(TimeSpan.FromSeconds(5)));
            await Execute(Tests.NamedPipeMerge, () => ConnectionFactory.ConnectNamedPipe("grpctest_merge").AsFrames(true).CreateChannelAsync(TimeSpan.FromSeconds(5)));
            await Execute(Tests.Tcp, () => ConnectionFactory.ConnectSocket(new IPEndPoint(IPAddress.Loopback, 10042)).AsStream().AsFrames().CreateChannelAsync(TimeSpan.FromSeconds(5)));
            await Execute(Tests.TcpSAEA, () => ConnectionFactory.ConnectSocket(new IPEndPoint(IPAddress.Loopback, 10042)).AsFrames().CreateChannelAsync(TimeSpan.FromSeconds(5)));
            await Execute(Tests.TcpTls, () => ConnectionFactory.ConnectSocket(new IPEndPoint(IPAddress.Loopback, 10043)).AsStream().WithTls(trustAny).AuthenticateAsClient("mytestserver").AsFrames().CreateChannelAsync(TimeSpan.FromSeconds(5)));
            await Execute(Tests.TcpTlsClientCert, () => ConnectionFactory.ConnectSocket(new IPEndPoint(IPAddress.Loopback, 10045)).AsStream().WithTls(trustAny, selectCert).AuthenticateAsClient("mytestserver").AsFrames().CreateChannelAsync(TimeSpan.FromSeconds(5)));
            await Execute(Tests.NamedPipeTls, () => ConnectionFactory.ConnectNamedPipe("grpctest_tls").WithTls(trustAny).AuthenticateAsClient("mytestserver").AsFrames().CreateChannelAsync(TimeSpan.FromSeconds(50)));
            await Execute(Tests.Managed, () => new ValueTask<GrpcChannel>(GrpcChannel.ForAddress("http://localhost:5074", grpcChannelOptions)), runClientStreaming: ManagedClientStreaming);
            await Execute(Tests.ManagedTls, () => new ValueTask<GrpcChannel>(GrpcChannel.ForAddress("https://localhost:7074", grpcChannelOptions)), runClientStreaming: ManagedClientStreaming);
            await Execute(Tests.Unmanaged, () => new ValueTask<Channel>(new Channel("localhost", 5074, ChannelCredentials.Insecure)), after: channel => new ValueTask(channel.ShutdownAsync()));

            LiteServer? localServer = null;
            await Execute(Tests.Local, () =>
            {
                localServer = new LiteServer();
                localServer.Bind<MyService>();
                return new ValueTask<LiteChannel>(localServer.CreateLocalClient());
            }, after: _ =>
            {
                localServer?.Stop();
                localServer = null;
                return default;
            });
         


            Console.WriteLine();
            Console.WriteLine("| Scenario | Unary (seq) | Unary (con) | Client-Streaming (b) | Client-Streaming (n) | Server-Streaming (b) | Server-Streaming (n) | Duplex |");
            Console.WriteLine("| -------- | ----------- | ------------| -------------------- | -------------------- | -------------------- | -------------------- | ------ |");
            foreach (var pair in timings.OrderBy(x => x.Key))
            {
                var scenario = pair.Key;
                var data = pair.Value;
                Console.WriteLine($"| {scenario} | {data.unarySequential} | {data.unaryConcurrent} | {data.clientStreamingBuffered} | {data.clientStreamingNonBuffered} | {data.serverStreamingBuffered} | {data.serverStreamingNonBuffered} | {data.duplex} |");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Console.WriteLine("press any key");
            Console.ReadKey();
            return -1;
        }
    }
    static Task RunParallel(Func<Task> operation, int times = 1)
    {
        if (times == 0) return Task.CompletedTask;
        if (times == 1) return operation();
        var tasks = new Task[times];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = operation();
        return Task.WhenAll(tasks);
    }
    async static Task Run(ChannelBase channel, Tests test, int repeatCount, bool runClientStreaming = true)
    {
        try
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            var options = new CallOptions(cancellationToken: cts.Token);

            FooServiceClient client;
            try
            {
                var invoker = channel.CreateCallInvoker();
                Console.WriteLine($"Connecting to {channel.Target} ({test}, {invoker.GetType().Name})...");
                client = new FooServiceClient(invoker);

                using var call = client.UnaryAsync(new FooRequest { Value = 42 }, options);
                var result = await call.ResponseAsync;
                if (result?.Value != 42) throw new InvalidOperationException("Incorrect response received: " + result);
                Console.WriteLine("(validated)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return;
            }

            long unarySequential = 0, unaryConcurrent = 0, clientStreamingBuffered = 0, clientStreamingNonBuffered = 0, serverStreamingBuffered = 0, serverStreamingNonBuffered = 0, duplex = 0;

            try
            {
                for (int j = 0; j < repeatCount; j++)
                {
                    var watch = Stopwatch.StartNew();
                    const int OPCOUNT = 10000;
                    for (int i = 0; i < OPCOUNT; i++)
                    {
                        using var call = client.UnaryAsync(new FooRequest { Value = i }, options);
                        var result = await call.ResponseAsync;

                        if (result?.Value != i) throw new InvalidOperationException("Incorrect response received: " + result);
                    }
                    unarySequential += ShowTiming(nameof(client.UnaryAsync) + " (sequential)", watch, OPCOUNT);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                unarySequential = int.MinValue;
            }
            Console.WriteLine();


            try
            {
                for (int j = 0; j < repeatCount; j++)
                {
                    var watch = Stopwatch.StartNew();
                    const int OPCOUNT = 1000, CONCURRENCY = 10;
                    await RunParallel(async () =>
                    {
                        for (int i = 0; i < OPCOUNT; i++)
                        {
                            using var call = client.UnaryAsync(new FooRequest { Value = i }, options);
                            var result = await call.ResponseAsync;

                            if (result?.Value != i) throw new InvalidOperationException("Incorrect response received: " + result);
                        }
                    }, CONCURRENCY);
                    unaryConcurrent += ShowTiming(nameof(client.UnaryAsync) + " (concurrent)", watch, OPCOUNT * CONCURRENCY);
                }
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                unaryConcurrent = int.MinValue;
            }

            if (runClientStreaming)
            {
                try
                {
                    for (int j = 0; j < repeatCount; j++)
                    {
                        var watch = Stopwatch.StartNew();
                        using var call = client.ClientStreaming(options);
                        const int OPCOUNT = 50000;
                        int sum = 0;
                        call.RequestStream.WriteOptions = MyService.Buffered;
                        for (int i = 0; i < OPCOUNT; i++)
                        {
                            await call.RequestStream.WriteAsync(new FooRequest { Value = i });
                            sum += i;
                        }
                        await call.RequestStream.CompleteAsync();
                        var result = await call.ResponseAsync;
                        if (result?.Value != sum) throw new InvalidOperationException("Incorrect response received: " + result);
                        clientStreamingBuffered += ShowTiming(nameof(client.ClientStreaming) + " b", watch, OPCOUNT);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    clientStreamingBuffered = int.MinValue;
                }
                Console.WriteLine();

                try
                {
                    for (int j = 0; j < repeatCount; j++)
                    {
                        var watch = Stopwatch.StartNew();
                        using var call = client.ClientStreaming(options);
                        const int OPCOUNT = 50000;
                        int sum = 0;
                        call.RequestStream.WriteOptions = MyService.NonBuffered;
                        for (int i = 0; i < OPCOUNT; i++)
                        {
                            await call.RequestStream.WriteAsync(new FooRequest { Value = i });
                            sum += i;
                        }
                        await call.RequestStream.CompleteAsync();
                        var result = await call.ResponseAsync;
                        if (result?.Value != sum) throw new InvalidOperationException("Incorrect response received: " + result);
                        clientStreamingNonBuffered += ShowTiming(nameof(client.ClientStreaming) + " nb", watch, OPCOUNT);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    clientStreamingNonBuffered = int.MinValue;
                }
                Console.WriteLine();
            }
            else
            {
                clientStreamingNonBuffered = clientStreamingBuffered = int.MinValue;
            }

            try
            {
                for (int j = 0; j < repeatCount; j++)
                {
                    var watch = Stopwatch.StartNew();
                    const int OPCOUNT = 50000;
                    using var call = client.ServerStreaming(new FooRequest { Value = OPCOUNT }, options);
                    int count = 0;
                    while (await call.ResponseStream.MoveNext())
                    {
                        var result = call.ResponseStream.Current;
                        if (result?.Value != count) throw new InvalidOperationException("Incorrect response received: " + result);
                        count++;
                    }
                    if (count != OPCOUNT) throw new InvalidOperationException("Incorrect response count received: " + count);
                    serverStreamingBuffered += ShowTiming(nameof(client.ServerStreaming) + " b", watch, OPCOUNT);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                serverStreamingBuffered = int.MinValue;
            }
            Console.WriteLine();

            try
            {
                for (int j = 0; j < repeatCount; j++)
                {
                    var watch = Stopwatch.StartNew();
                    const int OPCOUNT = 50000;
                    using var call = client.ServerStreaming(new FooRequest { Value = -OPCOUNT }, options);
                    int count = 0;
                    while (await call.ResponseStream.MoveNext())
                    {
                        var result = call.ResponseStream.Current;
                        if (result?.Value != count) throw new InvalidOperationException("Incorrect response received: " + result);
                        count++;
                    }
                    if (count != OPCOUNT) throw new InvalidOperationException("Incorrect response count received: " + count);
                    serverStreamingNonBuffered += ShowTiming(nameof(client.ServerStreaming) + " nb", watch, OPCOUNT);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                serverStreamingNonBuffered = int.MinValue;
            }
            Console.WriteLine();

            if (runClientStreaming)
            {
                try
                {
                    for (int j = 0; j < repeatCount; j++)
                    {
                        var watch = Stopwatch.StartNew();
                        const int OPCOUNT = 10000;
                        using var call = client.Duplex(options);

                        for (int i = 0; i < OPCOUNT; i++)
                        {
                            await call.RequestStream.WriteAsync(new FooRequest { Value = i });
                            if (!await call.ResponseStream.MoveNext()) throw new InvalidOperationException("Duplex stream terminated early");
                            var result = call.ResponseStream.Current;
                            if (result?.Value != i) throw new InvalidOperationException("Incorrect response received: " + result);
                        }
                        await call.RequestStream.CompleteAsync();
                        if (await call.ResponseStream.MoveNext()) throw new InvalidOperationException("Duplex stream ran over");
                        duplex += ShowTiming(nameof(client.Duplex), watch, OPCOUNT);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    duplex = int.MinValue;
                }
                Console.WriteLine();
            }
            else
            {
                duplex = int.MinValue;
            }

            // store the average nanos-per-op
            timings.Add(test.ToString(), (
                AutoScale(unarySequential / repeatCount, true),
                AutoScale(unaryConcurrent / repeatCount, true),
                AutoScale(clientStreamingBuffered / repeatCount, true),
                AutoScale(clientStreamingNonBuffered / repeatCount, true),
                AutoScale(serverStreamingBuffered / repeatCount, true),
                AutoScale(serverStreamingNonBuffered / repeatCount, true),
                AutoScale(duplex / repeatCount, true)
            ));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{channel.Target}]: {ex.Message}");
        }
        finally
        {
            try { await channel.ShutdownAsync(); }
            catch { }
        }

        static long ShowTiming(string label, Stopwatch watch, int operations)
        {
            watch.Stop();
            var nanos = (watch.ElapsedTicks * 1_000_000_000) / Stopwatch.Frequency;
            Console.WriteLine($"{label} ×{operations}: {AutoScale(nanos)}, {AutoScale(nanos / operations)}/op");
            return nanos / operations;
        }
        static string AutoScale(long nanos, bool forceNanos = false)
        {
            if (nanos < 0) return "n/a";
            long qty = nanos;
            if (forceNanos) return $"{qty:###,###,##0}ns";
            if (qty < 10000) return $"{qty:#,##0}ns";
            qty /= 1000;
            if (qty < 10000) return $"{qty:#,##0}μs";
            qty /= 1000;
            if (qty < 10000) return $"{qty:#,##0}ms";

            return TimeSpan.FromMilliseconds(qty).ToString();
        }
    }

}