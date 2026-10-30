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
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpOnvifClient;
using SharpOnvifCommon;
using SharpOnvifCommon.Onvif;
using SharpOnvifCommon.Soap;
using SharpOnvifServer.Dispatch;
using SharpOnvifServer.Events;

namespace SharpOnvif.Tests
{
    /// <summary>
    /// A subscription's calls, addressed the way the device asked, arriving at a device.
    /// <para>
    /// The client side of this is pinned on its own; this is the round trip. The subscribe hands
    /// back a reference with a reference parameter in it, the convenience client pulls with that
    /// reference, and the device sees the headers arrive - and still dispatches the request, which
    /// is the part a header the server never used to receive could have broken.
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class TestSubscriptionAddressing
    {
        private const string DevicePath = "/onvif/device_service";
        private const string EventsPath = "/onvif/Events/PullPointSubscription";

        private const string SubscriptionId = "sub-7";
        private const string ParameterNamespace = "urn:example:subscription";

        private static readonly XNamespace Wsa = EndpointReference.Namespace;
        private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";

        private static WebApplication _app;
        private static string _baseAddress;

        private sealed class DeviceImpl : SharpOnvifServer.DeviceMgmt.DeviceBase
        {
            public override SharpOnvifServer.DeviceMgmt.GetServicesResponse GetServices(
                SharpOnvifServer.DeviceMgmt.GetServicesRequest request)
            {
                return new SharpOnvifServer.DeviceMgmt.GetServicesResponse
                {
                    Service = new[]
                    {
                        new SharpOnvifServer.DeviceMgmt.Service
                        {
                            Namespace = OnvifServices.DEVICE_MGMT,
                            XAddr = _baseAddress + DevicePath,
                            Version = new OnvifVersion { Major = 2, Minor = 6 },
                        },
                        new SharpOnvifServer.DeviceMgmt.Service
                        {
                            Namespace = OnvifServices.EVENTS,
                            XAddr = _baseAddress + EventsPath,
                            Version = new OnvifVersion { Major = 2, Minor = 6 },
                        },
                    },
                };
            }
        }

        /// <summary>
        /// Hands out a subscription with a reference parameter, and keeps the pull that comes back
        /// for it.
        /// </summary>
        private sealed class EventsImpl : EventsBase
        {
            public static string LastPullEnvelope;
            public static string LastPullSubscriptionId;

            public override CreatePullPointSubscriptionResponse CreatePullPointSubscription(
                CreatePullPointSubscriptionRequest request)
            {
                var document = new XmlDocument();
                XmlElement parameter = document.CreateElement("SubscriptionId", ParameterNamespace);
                parameter.InnerText = SubscriptionId;

                return new CreatePullPointSubscriptionResponse
                {
                    SubscriptionReference = new EndpointReferenceType
                    {
                        Address = new AttributedURIType { Value = _baseAddress + EventsPath + "/" + SubscriptionId + "/" },
                        ReferenceParameters = new ReferenceParametersType { Any = new[] { parameter } },
                    },
                    CurrentTime = DateTime.UtcNow,
                    TerminationTime = DateTime.UtcNow.AddMinutes(1),
                };
            }

            public override async Task<PullMessagesResponse> PullMessagesAsync(
                PullMessagesRequest request, CancellationToken cancellationToken)
            {
                HttpContext context = OnvifOperationContext.Current;

                // What arrived, as it arrived: the endpoint has read the body already, so this is
                // the copy it kept rather than a second read.
                LastPullEnvelope = Encoding.UTF8.GetString(await OnvifRequestBody.ReadAsync(context));
                LastPullSubscriptionId = context.Items[SharpOnvifServer.Events.OnvifEvents.ONVIF_SUBSCRIPTION_ID] as string;

                return new PullMessagesResponse
                {
                    CurrentTime = DateTime.UtcNow,
                    TerminationTime = DateTime.UtcNow.AddMinutes(1),
                    NotificationMessage = Array.Empty<NotificationMessageHolderType>(),
                };
            }
        }

        [ClassInitialize]
        public static async Task StartServer(TestContext context)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            builder.Services.AddSingleton<DeviceImpl>();
            builder.Services.AddSingleton<EventsImpl>();

            _app = builder.Build();
            _app.MapOnvifService<DeviceImpl>(DevicePath);
            _app.MapOnvifService<EventsImpl>(EventsPath);

            await _app.StartAsync();

            _baseAddress = _app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>().Addresses.First().TrimEnd('/');
        }

        [ClassCleanup]
        public static async Task StopServer()
        {
            if (_app != null) await _app.StopAsync();
        }

        [TestMethod]
        public async Task PullsArriveAddressedToTheSubscriptionTheDeviceHandedOut()
        {
            using (var client = new SimpleOnvifClient(_baseAddress + DevicePath))
            {
                var subscription = await client.PullPointSubscribeAsync(60);

                Assert.IsNotNull(subscription.SubscriptionReference?.ReferenceParameters?.Any,
                    "the reference parameter has to survive the trip to the client for the rest of this to mean anything");

                var messages = await client.PullPointPullMessagesAsync(subscription.SubscriptionReference, 1, 10);
                Assert.IsNotNull(messages, "the device did not answer the pull");
            }

            // Dispatched: to the subscription the address named, with the headers intact.
            Assert.AreEqual(SubscriptionId, EventsImpl.LastPullSubscriptionId,
                "the pull did not reach the subscription its address names");

            XElement header = XDocument.Parse(EventsImpl.LastPullEnvelope).Root.Element(Soap + "Header");
            Assert.IsNotNull(header, "the pull arrived with no header");
            Assert.AreEqual(_baseAddress + EventsPath + "/" + SubscriptionId + "/", header.Element(Wsa + "To")?.Value,
                "To is the address the device gave, as it gave it");
            Assert.AreEqual("http://www.onvif.org/ver10/events/wsdl/PullPointSubscription/PullMessagesRequest",
                header.Element(Wsa + "Action")?.Value);

            XElement parameter = header.Element((XNamespace)ParameterNamespace + "SubscriptionId");
            Assert.IsNotNull(parameter, "the reference parameter did not come back");
            Assert.AreEqual(SubscriptionId, parameter.Value);
            Assert.AreEqual("true", (string)parameter.Attribute(Wsa + "IsReferenceParameter"));
        }
    }
}
