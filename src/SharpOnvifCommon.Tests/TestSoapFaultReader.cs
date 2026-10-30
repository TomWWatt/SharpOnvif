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

using System.IO;
using System.Xml;
using SharpOnvifCommon.Xml;

namespace SharpOnvif.Tests
{
    /// <summary>
    /// Reading a fault as devices write them: all on one line, with nothing between one element
    /// and the next.
    /// <para>
    /// The reader stepped once too far after each Value it read, which an indented fault hid -
    /// the extra step landed on whitespace. Written without any, the step landed on the Reason,
    /// which was then consumed inside the Code and lost: the exception for a fault with no
    /// subcode said "Sender" and nothing else.
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class TestSoapFaultReader
    {
        private const string EnvelopeNamespace = "http://www.w3.org/2003/05/soap-envelope";

        private static SoapFault Read(string fault)
        {
            string envelope =
                "<SOAP-ENV:Envelope xmlns:SOAP-ENV=\"" + EnvelopeNamespace + "\"><SOAP-ENV:Body>" +
                fault +
                "</SOAP-ENV:Body></SOAP-ENV:Envelope>";

            using (XmlReader reader = SoapEnvelope.CreateReader(new StringReader(envelope)))
            {
                var raised = Assert.ThrowsExactly<SoapFaultException>(() => SoapEnvelope.MoveToBody(reader));
                Assert.IsNotNull(raised.Fault, "the body carries a fault, and it has to be read as one");
                return raised.Fault;
            }
        }

        [TestMethod]
        public void KeepsTheReasonOfACompactFaultWithNoSubcode()
        {
            SoapFault fault = Read(
                "<SOAP-ENV:Fault>" +
                "<SOAP-ENV:Code><SOAP-ENV:Value>SOAP-ENV:Sender</SOAP-ENV:Value></SOAP-ENV:Code>" +
                "<SOAP-ENV:Reason><SOAP-ENV:Text xml:lang=\"en\">Not Authorized</SOAP-ENV:Text></SOAP-ENV:Reason>" +
                "</SOAP-ENV:Fault>");

            Assert.AreEqual("Sender", fault.Code);
            Assert.AreEqual(0, fault.Subcodes.Count);
            Assert.AreEqual("Not Authorized", fault.Reason, "the reason is the whole of what this fault says");
            Assert.AreEqual("Not Authorized", fault.ToString(), "and what the exception says");
        }

        [TestMethod]
        public void ReadsTheSubcodeChainOfACompactFault()
        {
            SoapFault fault = Read(
                "<SOAP-ENV:Fault>" +
                "<SOAP-ENV:Code><SOAP-ENV:Value>SOAP-ENV:Sender</SOAP-ENV:Value>" +
                "<SOAP-ENV:Subcode xmlns:ter=\"http://www.onvif.org/ver10/error\"><SOAP-ENV:Value>ter:InvalidArgVal</SOAP-ENV:Value>" +
                "<SOAP-ENV:Subcode><SOAP-ENV:Value>ter:NoProfile</SOAP-ENV:Value></SOAP-ENV:Subcode>" +
                "</SOAP-ENV:Subcode></SOAP-ENV:Code>" +
                "<SOAP-ENV:Reason><SOAP-ENV:Text xml:lang=\"en\">No such profile</SOAP-ENV:Text></SOAP-ENV:Reason>" +
                "</SOAP-ENV:Fault>");

            Assert.AreEqual("Sender", fault.Code);
            CollectionAssert.AreEqual(new[] { "InvalidArgVal", "NoProfile" }, (System.Collections.ICollection)fault.Subcodes);
            Assert.AreEqual("NoProfile", fault.Subcode);
            Assert.AreEqual("No such profile", fault.Reason);
        }

        [TestMethod]
        public void ReadsADetailThatEndsTheFault()
        {
            SoapFault fault = Read(
                "<SOAP-ENV:Fault>" +
                "<SOAP-ENV:Code><SOAP-ENV:Value>SOAP-ENV:Receiver</SOAP-ENV:Value></SOAP-ENV:Code>" +
                "<SOAP-ENV:Reason><SOAP-ENV:Text xml:lang=\"en\">Busy</SOAP-ENV:Text></SOAP-ENV:Reason>" +
                "<SOAP-ENV:Detail><Retry xmlns=\"urn:example\">later</Retry></SOAP-ENV:Detail>" +
                "</SOAP-ENV:Fault>");

            Assert.AreEqual("Receiver", fault.Code);
            Assert.AreEqual("Busy", fault.Reason);
            StringAssert.Contains(fault.Detail, "later");
        }

        [TestMethod]
        public void ReadsAnIndentedFaultTheSameWay()
        {
            // What worked all along, and has to keep working.
            SoapFault fault = Read(
                "\n  <SOAP-ENV:Fault>\n" +
                "    <SOAP-ENV:Code>\n      <SOAP-ENV:Value>SOAP-ENV:Sender</SOAP-ENV:Value>\n    </SOAP-ENV:Code>\n" +
                "    <SOAP-ENV:Reason>\n      <SOAP-ENV:Text xml:lang=\"en\">Not Authorized</SOAP-ENV:Text>\n    </SOAP-ENV:Reason>\n" +
                "  </SOAP-ENV:Fault>\n");

            Assert.AreEqual("Sender", fault.Code);
            Assert.AreEqual("Not Authorized", fault.Reason);
        }

        [TestMethod]
        public void SaysNothingForAFaultThatSaysNothing()
        {
            SoapFault fault = Read("<SOAP-ENV:Fault><SOAP-ENV:Code><SOAP-ENV:Value>SOAP-ENV:Sender</SOAP-ENV:Value></SOAP-ENV:Code></SOAP-ENV:Fault>");

            Assert.AreEqual("Sender", fault.Code);
            Assert.IsNull(fault.Reason);
            Assert.AreEqual("Sender", fault.ToString(), "the code is all there is");
        }
    }
}
