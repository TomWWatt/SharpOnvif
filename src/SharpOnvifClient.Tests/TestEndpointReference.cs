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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using SharpOnvifClient;
using SharpOnvifClient.DeviceMgmt;
using SharpOnvifCommon.Onvif;
using SharpOnvifCommon.Security;
using SharpOnvifCommon.Soap;

namespace SharpOnvif.Tests
{
    /// <summary>
    /// The WS-Addressing headers a client puts on a message when it is given an endpoint
    /// reference.
    /// <para>
    /// A device answers a subscribe with a reference - an address, and sometimes reference
    /// parameters - and some devices find the subscription by nothing else: a pull that arrives
    /// without the headers is refused, however right its address. The client used to send only
    /// the body, so against such a device every pull failed.
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class TestEndpointReference
    {
        private const string Endpoint = "http://192.168.1.10/onvif/device_service";

        /// <summary>A subscription address as devices give them, query string included.</summary>
        private const string SubscriptionAddress = "http://192.168.1.10/onvif/Events/PullPointSubscription?Idx=3";

        private const string ParameterNamespace = "urn:example:subscription";

        private static readonly XNamespace Wsa = EndpointReference.Namespace;
        private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";

        private const string DeviceInformationReply =
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" +
            "<tds:GetDeviceInformationResponse xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\">" +
            "<tds:Manufacturer>ACME</tds:Manufacturer></tds:GetDeviceInformationResponse>" +
            "</s:Body></s:Envelope>";

        private const string PullMessagesReply =
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" +
            "<tev:PullMessagesResponse xmlns:tev=\"http://www.onvif.org/ver10/events/wsdl\">" +
            "<tev:CurrentTime>2026-01-01T00:00:00Z</tev:CurrentTime>" +
            "<tev:TerminationTime>2026-01-01T00:01:00Z</tev:TerminationTime>" +
            "</tev:PullMessagesResponse></s:Body></s:Envelope>";

        /// <summary>Answers every request with the same reply, and keeps what it was sent.</summary>
        private sealed class RecordingTransport : HttpMessageHandler
        {
            private readonly string _reply;

            public RecordingTransport(string reply) { _reply = reply; }

            public List<string> Requests { get; } = new List<string>();

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(await request.Content.ReadAsStringAsync().ConfigureAwait(false));

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_reply) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
                return response;
            }
        }

        /// <summary>The convenience client, sending through a transport of the test's choosing.</summary>
        private sealed class RecordedClient : SimpleOnvifClient
        {
            public RecordedClient(HttpMessageHandler transport) : base(Endpoint)
            {
                _settings.Transport = transport;
            }
        }

        private static XmlElement Parameter(string value)
        {
            var document = new XmlDocument();
            XmlElement element = document.CreateElement("SubscriptionId", ParameterNamespace);
            element.InnerText = value;
            return element;
        }

        private static XElement Header(string envelope)
        {
            return XDocument.Parse(envelope).Root.Element(Soap + "Header");
        }

        [TestMethod]
        public async Task AddressesEveryMessageToTheReference()
        {
            var transport = new RecordingTransport(DeviceInformationReply);

            using (var client = new DeviceClient(Endpoint, new OnvifClientSettings { Transport = transport })
            {
                EndpointReference = new EndpointReference(SubscriptionAddress, new[] { Parameter("42") }),
            })
            {
                await client.GetDeviceInformationAsync();
            }

            XElement header = Header(transport.Requests.Single());
            Assert.IsNotNull(header, "the message carries no header at all");

            Assert.AreEqual("http://www.onvif.org/ver10/device/wsdl/GetDeviceInformation",
                header.Element(Wsa + "Action")?.Value, "the action the message is sent under");
            Assert.AreEqual(SubscriptionAddress, header.Element(Wsa + "To")?.Value,
                "To is the address as the device gave it, query string and all");
            Assert.AreEqual(EndpointReference.AnonymousAddress,
                header.Element(Wsa + "ReplyTo")?.Element(Wsa + "Address")?.Value,
                "the reply comes back on the connection");

            string messageId = header.Element(Wsa + "MessageID")?.Value;
            StringAssert.StartsWith(messageId, "urn:uuid:");
            Assert.IsTrue(Guid.TryParse(messageId.Substring("urn:uuid:".Length), out _), $"'{messageId}' is not a uuid");

            XElement parameter = header.Element((XNamespace)ParameterNamespace + "SubscriptionId");
            Assert.IsNotNull(parameter, "a reference parameter goes back as a header of its own");
            Assert.AreEqual("42", parameter.Value);
            Assert.AreEqual("true", (string)parameter.Attribute(Wsa + "IsReferenceParameter"),
                "marked as the reference parameter it is");
        }

        [TestMethod]
        public async Task GivesEachMessageAnIdOfItsOwn()
        {
            var transport = new RecordingTransport(DeviceInformationReply);

            using (var client = new DeviceClient(Endpoint, new OnvifClientSettings { Transport = transport })
            {
                EndpointReference = new EndpointReference(SubscriptionAddress),
            })
            {
                await client.GetDeviceInformationAsync();
                await client.GetDeviceInformationAsync();
            }

            string first = Header(transport.Requests[0]).Element(Wsa + "MessageID").Value;
            string second = Header(transport.Requests[1]).Element(Wsa + "MessageID").Value;

            Assert.AreNotEqual(first, second, "a message id names one message");
        }

        [TestMethod]
        public async Task SendsNoHeaderWithoutAReference()
        {
            // What every client sent before this existed, and what a client given no reference
            // still sends: no header element at all, rather than an empty one.
            var transport = new RecordingTransport(DeviceInformationReply);

            using (var client = new DeviceClient(Endpoint, new OnvifClientSettings { Transport = transport }))
            {
                await client.GetDeviceInformationAsync();
            }

            Assert.IsNull(Header(transport.Requests.Single()));
        }

        [TestMethod]
        public async Task KeepsTheCredentialsBesideTheAddressing()
        {
            // Addressing must not displace the security header: a subscription on a device that
            // wants a password per call needs both.
            var transport = new RecordingTransport(DeviceInformationReply);

            using (var client = new DeviceClient(Endpoint, new OnvifClientSettings
            {
                Transport = transport,
                Credentials = new NetworkCredential("admin", "password"),
                Authentication = new OnvifAuthenticationSettings(DigestAuthentication.WsUsernameToken),
            })
            {
                EndpointReference = new EndpointReference(SubscriptionAddress),
            })
            {
                await client.GetDeviceInformationAsync();
            }

            XElement header = Header(transport.Requests.Single());
            Assert.AreEqual(SubscriptionAddress, header.Element(Wsa + "To")?.Value);
            Assert.IsNotNull(header.Elements().FirstOrDefault(e => e.Name.LocalName == "Security"),
                "the UsernameToken is still there");
        }

        [TestMethod]
        public async Task PullsWithTheReferenceTheDeviceHandedBack()
        {
            var transport = new RecordingTransport(PullMessagesReply);
            var reference = new EndpointReferenceType
            {
                Address = new AttributedURIType { Value = SubscriptionAddress },
                ReferenceParameters = new ReferenceParametersType { Any = new[] { Parameter("42") } },
            };

            using (var client = new RecordedClient(transport))
            {
                await client.PullPointPullMessagesAsync(reference, 1, 10);
            }

            XElement header = Header(transport.Requests.Single());
            Assert.IsNotNull(header, "the pull carries no header at all");
            Assert.AreEqual(SubscriptionAddress, header.Element(Wsa + "To")?.Value);
            Assert.AreEqual("http://www.onvif.org/ver10/events/wsdl/PullPointSubscription/PullMessagesRequest",
                header.Element(Wsa + "Action")?.Value);
            Assert.AreEqual("42", header.Element((XNamespace)ParameterNamespace + "SubscriptionId")?.Value,
                "the reference parameter the device put in the subscription goes back on every pull");
        }

        [TestMethod]
        public async Task PullsByAddressAloneWhenThatIsAllItWasGiven()
        {
            // The older overload takes the address. It still addresses the pull - To, Action and
            // the rest - it just has no reference parameters to send.
            var transport = new RecordingTransport(PullMessagesReply);

            using (var client = new RecordedClient(transport))
            {
                await client.PullPointPullMessagesAsync(SubscriptionAddress, 1, 10);
            }

            XElement header = Header(transport.Requests.Single());
            Assert.IsNotNull(header);
            Assert.AreEqual(SubscriptionAddress, header.Element(Wsa + "To")?.Value);
            Assert.IsNull(header.Element((XNamespace)ParameterNamespace + "SubscriptionId"));
        }

        [TestMethod]
        public void RefusesAReferenceWithoutAnAddress()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => new EndpointReference(null));
            Assert.ThrowsExactly<ArgumentNullException>(() => new EndpointReference(" "));
        }
    }
}
