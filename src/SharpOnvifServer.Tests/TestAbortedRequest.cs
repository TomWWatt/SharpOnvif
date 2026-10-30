// SharpOnvif
// Copyright (C) 2026 Lukas Volf
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpOnvifServer.DeviceMgmt;
using SharpOnvifServer.Dispatch;

namespace SharpOnvif.Tests
{
    /// <summary>
    /// A request the client dropped while the device was still working on it.
    /// <para>
    /// A pull point spends nearly all of its time in exactly this position: the client holds a
    /// request open for up to a minute, and a client that is stopped, or has lost interest, closes
    /// the connection mid-wait. The operation's token is cancelled, which is right - and the
    /// endpoint then logged the cancellation at error as a failed operation and wrote a fault to a
    /// connection nobody was reading. A device that is polled all day filled its log with its
    /// clients' comings and goings.
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class TestAbortedRequest
    {
        private const string EndpointPath = "/onvif/device_service";

        private const string DeviceInformationAction = "http://www.onvif.org/ver10/device/wsdl/GetDeviceInformation";

        private const string DeviceInformationBody =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
            "<s:Body><GetDeviceInformation xmlns=\"http://www.onvif.org/ver10/device/wsdl\"/></s:Body></s:Envelope>";

        /// <summary>Keeps every warning and error the host logs, so a test can say there were none.</summary>
        private sealed class CapturingLoggerProvider : ILoggerProvider
        {
            public ConcurrentQueue<string> Entries { get; } = new ConcurrentQueue<string>();

            public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

            public void Dispose() { }

            private sealed class Logger : ILogger
            {
                private readonly string _category;
                private readonly ConcurrentQueue<string> _entries;

                public Logger(string category, ConcurrentQueue<string> entries)
                {
                    _category = category;
                    _entries = entries;
                }

                public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                {
                    if (!IsEnabled(logLevel)) return;

                    _entries.Enqueue(
                        logLevel + " " + _category + ": " + formatter(state, exception) +
                        (exception == null ? "" : " [" + exception.GetType().Name + ": " + exception.Message + "]"));
                }
            }
        }

        /// <summary>
        /// Answers slowly, the way a pull does, and says what it saw of the request on the way.
        /// </summary>
        private sealed class SlowDeviceImpl : DeviceBase
        {
            public TaskCompletionSource<bool> Entered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Cancelled { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Finished { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public override async Task<GetDeviceInformationResponse> GetDeviceInformationAsync(
                GetDeviceInformationRequest request, CancellationToken cancellationToken)
            {
                HttpContext context = OnvifOperationContext.Current;
                context.Response.OnCompleted(() =>
                {
                    Finished.TrySetResult(true);
                    return Task.CompletedTask;
                });

                Entered.TrySetResult(true);

                try
                {
                    // A pull waits like this for something to report. Here nothing ever comes,
                    // and the client goes away first.
                    await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.TrySetResult(true);
                    throw;
                }

                return new GetDeviceInformationResponse { Manufacturer = "ACME" };
            }
        }

        [TestMethod]
        [Timeout(60000)]
        public async Task LetsARequestTheClientDroppedEndQuietly()
        {
            var logs = new CapturingLoggerProvider();
            var impl = new SlowDeviceImpl();

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(impl);

            WebApplication app = builder.Build();
            app.MapOnvifService<SlowDeviceImpl>(EndpointPath);
            await app.StartAsync();

            try
            {
                string endpoint = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>().Addresses.First().TrimEnd('/') + EndpointPath;

                using (var http = new HttpClient())
                using (var gone = new CancellationTokenSource())
                {
                    var content = new StringContent(DeviceInformationBody);
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                        "application/soap+xml; charset=utf-8; action=\"" + DeviceInformationAction + "\"");

                    Task<HttpResponseMessage> pending = http.PostAsync(endpoint, content, gone.Token);

                    await impl.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                    // The client gives up: the connection is closed with the request still open.
                    gone.Cancel();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
                }

                await impl.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await impl.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                // Stopping the host is what flushes whatever the connection's end has to say.
                await app.StopAsync();
                await app.DisposeAsync();
            }

            string[] entries = logs.Entries.ToArray();
            string report = entries.Length == 0 ? "(nothing was logged)" : string.Join(Environment.NewLine, entries);

            Assert.IsFalse(entries.Any(e => e.StartsWith("Error", StringComparison.Ordinal)),
                "a client that went away is not a failure of the device, and must not be logged as one:" + Environment.NewLine + report);
            Assert.IsFalse(entries.Any(e => e.StartsWith("Warning", StringComparison.Ordinal)),
                "nor is it anything to warn about:" + Environment.NewLine + report);
        }
    }
}
