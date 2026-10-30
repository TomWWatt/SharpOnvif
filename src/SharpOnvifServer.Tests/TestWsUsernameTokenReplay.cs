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
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using SharpOnvifClient.DeviceMgmt;
using SharpOnvifCommon.Security;
using SharpOnvifCommon.Soap;
using SharpOnvifCommon.Xml;

namespace SharpOnvif.Tests
{
    /// <summary>
    /// A WS-UsernameToken captured on the wire must not be good a second time.
    /// <para>
    /// The Created time bounds how long a token stays fresh, and nothing else about it changes
    /// between one sending and the next: the same bytes, sent again inside that window, used to
    /// authenticate again. The token profile has the device remember the nonces it has accepted
    /// for exactly this reason.
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class TestWsUsernameTokenReplay
    {
        private const string DeviceNamespace = "http://www.onvif.org/ver10/device/wsdl";
        private const string Action = DeviceNamespace + "/GetDeviceInformation";

        private const string PasswordDigestType =
            "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";

        /// <summary>One request, as the client would send it, so that it can be sent more than once.</summary>
        private static string Envelope()
        {
            return SoapEnvelope.Write(
                OnvifXmlNamespaces.EnvelopePrologue,
                writer => WsUsernameToken.Write(
                    writer, AuthenticatedDevice.UserName, AuthenticatedDevice.Password, TimeSpan.Zero),
                WriteBody);
        }

        /// <summary>
        /// The same request with a token of the test's own making, for when the nonce has to be
        /// chosen rather than drawn.
        /// </summary>
        private static string Envelope(string nonce, string password)
        {
            string created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            string digest = WsDigestAuthentication.CreateSoapDigest(nonce, created, password);

            return SoapEnvelope.Write(OnvifXmlNamespaces.EnvelopePrologue, writer =>
            {
                XmlWriter xml = writer.Xml;
                xml.WriteStartElement("wsse", "Security", WsUsernameToken.SecurityExtNamespace);
                xml.WriteStartElement("wsse", "UsernameToken", WsUsernameToken.SecurityExtNamespace);
                xml.WriteElementString("wsse", "Username", WsUsernameToken.SecurityExtNamespace, AuthenticatedDevice.UserName);
                xml.WriteStartElement("wsse", "Password", WsUsernameToken.SecurityExtNamespace);
                xml.WriteAttributeString("Type", PasswordDigestType);
                xml.WriteString(digest);
                xml.WriteEndElement();
                xml.WriteElementString("wsse", "Nonce", WsUsernameToken.SecurityExtNamespace, nonce);
                xml.WriteElementString("wsu", "Created", WsUsernameToken.SecurityUtilityNamespace, created);
                xml.WriteEndElement();
                xml.WriteEndElement();
            }, WriteBody);
        }

        private static void WriteBody(IXmlWriter writer)
        {
            writer.WriteStartElement(DeviceNamespace, "GetDeviceInformation");
            writer.WriteEndElement();
        }

        private static async Task<HttpStatusCode> PostAsync(AuthenticatedDevice device, string envelope)
        {
            using (var http = new HttpClient())
            {
                var content = new StringContent(envelope);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                    "application/soap+xml; charset=utf-8; action=\"" + Action + "\"");

                using (HttpResponseMessage response = await http.PostAsync(device.Endpoint, content))
                {
                    return response.StatusCode;
                }
            }
        }

        /// <summary>Remembers what it was asked to spend, and spends it like the default store.</summary>
        private sealed class RecordingStore : INonceReplayStore
        {
            private readonly MemoryNonceReplayStore _inner = new MemoryNonceReplayStore();

            public List<(string Nonce, int Count, DateTimeOffset ExpiresAt)> Spent { get; } =
                new List<(string, int, DateTimeOffset)>();

            public Task<bool> TryUseNonceAsync(string nonce, int nonceCount, DateTimeOffset expiresAt, CancellationToken cancellationToken)
            {
                lock (Spent) Spent.Add((nonce, nonceCount, expiresAt));
                return _inner.TryUseNonceAsync(nonce, nonceCount, expiresAt, cancellationToken);
            }
        }

        [TestMethod]
        public async Task RefusesTheSameTokenSentASecondTime()
        {
            await using var device = await AuthenticatedDevice.StartAsync(
                options => options.Onvif.Authentication = DigestAuthentication.WsUsernameToken);

            string captured = Envelope();

            Assert.AreEqual(HttpStatusCode.OK, await PostAsync(device, captured), "the first sending is the genuine one");
            Assert.AreEqual(HttpStatusCode.Unauthorized, await PostAsync(device, captured),
                "the same request again is a replay, however fresh its Created time still is");
        }

        [TestMethod]
        public async Task StillTakesAFreshTokenFromTheSameUser()
        {
            // Refusing a nonce must not refuse the user: every genuine request carries a new one.
            await using var device = await AuthenticatedDevice.StartAsync(
                options => options.Onvif.Authentication = DigestAuthentication.WsUsernameToken);

            string captured = Envelope();
            await PostAsync(device, captured);
            await PostAsync(device, captured);

            Assert.AreEqual(HttpStatusCode.OK, await PostAsync(device, Envelope()));

            using var client = new DeviceClient(device.Endpoint, new OnvifClientSettings
            {
                Credentials = new NetworkCredential(AuthenticatedDevice.UserName, AuthenticatedDevice.Password),
                Authentication = new OnvifAuthenticationSettings(DigestAuthentication.WsUsernameToken),
            });

            for (int call = 0; call < 3; call++)
            {
                var info = await client.GetDeviceInformationAsync();
                Assert.AreEqual(AuthenticatedDevice.Manufacturer, info.Manufacturer, $"call {call}");
            }
        }

        [TestMethod]
        public async Task RefusesAReplayWhenTheDeviceOffersBothSchemes()
        {
            // With HTTP Digest on as well the token is checked on a second path, and a replay has
            // to be caught there too.
            await using var device = await AuthenticatedDevice.StartAsync(options =>
                options.Onvif.Authentication = DigestAuthentication.WsUsernameToken | DigestAuthentication.HttpDigest);

            string captured = Envelope();

            Assert.AreEqual(HttpStatusCode.OK, await PostAsync(device, captured));
            Assert.AreEqual(HttpStatusCode.Unauthorized, await PostAsync(device, captured));
        }

        [TestMethod]
        public async Task DoesNotSpendTheNonceOfATokenItRefused()
        {
            // A refused request spends nothing, so a client that retries it - the HTTP Digest
            // handler sends the same body again after a challenge - is judged on the retry alone.
            // The same nonce under the right password is a different token, and it has to get in.
            await using var device = await AuthenticatedDevice.StartAsync(
                options => options.Onvif.Authentication = DigestAuthentication.WsUsernameToken);

            string nonce = WsDigestAuthentication.CalculateNonce();

            Assert.AreEqual(HttpStatusCode.Unauthorized, await PostAsync(device, Envelope(nonce, "not the password")));
            Assert.AreEqual(HttpStatusCode.OK, await PostAsync(device, Envelope(nonce, AuthenticatedDevice.Password)),
                "a nonce the device refused has not been spent");
            Assert.AreEqual(HttpStatusCode.Unauthorized, await PostAsync(device, Envelope(nonce, AuthenticatedDevice.Password)),
                "and once it has been accepted, it has");
        }

        [TestMethod]
        public async Task RemembersTheNonceForAsLongAsTheTokenStaysFresh()
        {
            // The store is told how long to keep the nonce. It is pointless to keep it past the
            // moment the clock check would refuse the token anyway, and unsafe to drop it sooner.
            var store = new RecordingStore();
            await using var device = await AuthenticatedDevice.StartAsync(options =>
            {
                options.Onvif.Authentication = DigestAuthentication.WsUsernameToken;
                options.WsUsernameTokenMaxTimeDeltaInMilliseconds = 120000;
                options.WsUsernameTokenNonceReplayStore = store;
            });

            DateTimeOffset before = DateTimeOffset.UtcNow;
            Assert.AreEqual(HttpStatusCode.OK, await PostAsync(device, Envelope()));
            DateTimeOffset after = DateTimeOffset.UtcNow;

            Assert.AreEqual(1, store.Spent.Count, "the store the options name is the one consulted");
            Assert.AreEqual(0, store.Spent[0].Count, "a token nonce has no count: it is spent once");
            Assert.IsTrue(store.Spent[0].ExpiresAt >= before.AddMinutes(2),
                $"forgotten at {store.Spent[0].ExpiresAt:O}, while the token stays fresh until {before.AddMinutes(2):O}");
            Assert.IsTrue(store.Spent[0].ExpiresAt <= after.AddMinutes(2).AddSeconds(1),
                "kept no longer than the token could pass the clock check");
        }

        [TestMethod]
        public async Task RefusesAReplayEvenWhenTheDeviceDoesNotCheckTheClock()
        {
            // A device that cannot keep time switches the clock check off, which leaves Created
            // bounding nothing. The nonce is still remembered, for the default window.
            var store = new RecordingStore();
            await using var device = await AuthenticatedDevice.StartAsync(options =>
            {
                options.Onvif.Authentication = DigestAuthentication.WsUsernameToken;
                options.WsUsernameTokenMaxTimeDeltaInMilliseconds = -1;
                options.WsUsernameTokenNonceReplayStore = store;
            });

            string captured = Envelope();

            Assert.AreEqual(HttpStatusCode.OK, await PostAsync(device, captured));
            Assert.AreEqual(HttpStatusCode.Unauthorized, await PostAsync(device, captured));
            Assert.IsTrue(store.Spent[0].ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(4),
                "with no clock check there is nothing else to bound the nonce, so it is kept for the default window");
        }
    }
}
