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

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using SharpOnvifClient.DeviceMgmt;
using SharpOnvifCommon.Soap;
using SharpOnvifCommon.Xml;

namespace SharpOnvif.Tests
{
    /// <summary>
    /// What a caller is told when the device says no.
    /// <para>
    /// A refusal arrives two ways: as a SOAP fault, or as a bare status with no fault behind it -
    /// a 401 from a device that challenges and nothing more, a page from a proxy. Either way the
    /// exception has to say what the device said and the status it said it with, because for a
    /// 401 without a fault the status is the only thing that says the credentials were refused.
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class TestFaultReporting
    {
        private const string Endpoint = "http://192.168.1.10/onvif/device_service";

        /// <summary>A device that answers every request the same way.</summary>
        private sealed class ScriptedTransport : HttpMessageHandler
        {
            private readonly HttpStatusCode _status;
            private readonly string _contentType;
            private readonly string _body;

            public ScriptedTransport(HttpStatusCode status, string contentType, string body)
            {
                _status = status;
                _contentType = contentType;
                _body = body;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(_status) { Content = new StringContent(_body) };
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(_contentType);
                return Task.FromResult(response);
            }
        }

        private static DeviceClient Client(HttpStatusCode status, string contentType, string body)
        {
            return new DeviceClient(Endpoint, new OnvifClientSettings
            {
                Transport = new ScriptedTransport(status, contentType, body),
            });
        }

        private const string CompactFault =
            "<SOAP-ENV:Envelope xmlns:SOAP-ENV=\"http://www.w3.org/2003/05/soap-envelope\"><SOAP-ENV:Body>" +
            "<SOAP-ENV:Fault><SOAP-ENV:Code><SOAP-ENV:Value>SOAP-ENV:Sender</SOAP-ENV:Value></SOAP-ENV:Code>" +
            "<SOAP-ENV:Reason><SOAP-ENV:Text xml:lang=\"en\">Not Authorized</SOAP-ENV:Text></SOAP-ENV:Reason>" +
            "</SOAP-ENV:Fault></SOAP-ENV:Body></SOAP-ENV:Envelope>";

        [TestMethod]
        public async Task CarriesTheStatusAndTheReasonOfAFault()
        {
            // A fault written the way devices write them: one line, no subcode, the reason
            // carrying the whole of what the device has to say.
            using (var client = Client(HttpStatusCode.BadRequest, "application/soap+xml; charset=utf-8", CompactFault))
            {
                var fault = await Assert.ThrowsExactlyAsync<SoapFaultException>(() => client.GetDeviceInformationAsync());

                Assert.AreEqual(HttpStatusCode.BadRequest, fault.StatusCode);
                Assert.AreEqual("Sender", fault.Fault.Code);
                Assert.AreEqual("Not Authorized", fault.Fault.Reason, "the reason survives the reading");
                StringAssert.Contains(fault.Message, "Not Authorized", "and is what the caller sees");
                Assert.IsFalse(fault.IsNotAuthorized, "a 400 with no NotAuthorized subcode is not a refusal of credentials");
            }
        }

        [TestMethod]
        public async Task CarriesTheStatusOfARefusalThatHasNoFault()
        {
            // The credentials refused by a device that puts nothing SOAP in its 401: the status
            // is all there is to go on, so it has to be there to go on.
            using (var client = Client(HttpStatusCode.Unauthorized, "text/html", "<html>Unauthorized</html>"))
            {
                var fault = await Assert.ThrowsExactlyAsync<SoapFaultException>(() => client.GetDeviceInformationAsync());

                Assert.AreEqual(HttpStatusCode.Unauthorized, fault.StatusCode);
                Assert.IsNull(fault.Fault, "there was no fault to parse");
                Assert.IsTrue(fault.IsNotAuthorized, "a 401 is the credentials refused, fault or no fault");
                StringAssert.Contains(fault.Message, "401");
            }
        }

        [TestMethod]
        public async Task TellsAFaultFromAFaultlessFourHundredAndOne()
        {
            // A 401 whose body is a fault naming NotAuthorized said so twice over already; the
            // status now says it for a fault that names nothing.
            using (var client = Client(HttpStatusCode.Unauthorized, "application/soap+xml; charset=utf-8", CompactFault))
            {
                var fault = await Assert.ThrowsExactlyAsync<SoapFaultException>(() => client.GetDeviceInformationAsync());

                Assert.AreEqual(HttpStatusCode.Unauthorized, fault.StatusCode);
                Assert.AreEqual("Not Authorized", fault.Fault.Reason);
                Assert.IsTrue(fault.IsNotAuthorized);
            }
        }

        [TestMethod]
        public async Task DoesNotCallEveryRefusalARefusalOfCredentials()
        {
            using (var client = Client(HttpStatusCode.InternalServerError, "text/plain", "out of memory"))
            {
                var fault = await Assert.ThrowsExactlyAsync<SoapFaultException>(() => client.GetDeviceInformationAsync());

                Assert.AreEqual(HttpStatusCode.InternalServerError, fault.StatusCode);
                Assert.IsFalse(fault.IsNotAuthorized);
            }
        }
    }
}
