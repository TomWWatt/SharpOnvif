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
using System.Xml;
using __RUNTIME__.Xml;

namespace __RUNTIME__.Soap
{
    /// <summary>
    /// Where a service said to send a message, as WS-Addressing puts it: an address, and the
    /// reference parameters that go back to it as headers.
    /// </summary>
    /// <remarks>
    /// A client given one addresses every message it sends: the action, a fresh message id, the
    /// anonymous reply address, To with the address exactly as the service gave it - query string
    /// and all - and each reference parameter, marked as one. A pull point subscription is what
    /// needs it. A device answers a subscribe with an endpoint reference, and some devices
    /// identify the subscription by those headers alone, refusing every pull, renew and
    /// unsubscribe that arrives without them.
    /// </remarks>
    public sealed class EndpointReference
    {
        /// <summary>WS-Addressing 1.0, which the SOAP 1.2 bindings use.</summary>
        public const string Namespace = "http://www.w3.org/2005/08/addressing";

        /// <summary>
        /// The reply address of a request/response exchange: the answer comes back on the
        /// connection the request went out on.
        /// </summary>
        public const string AnonymousAddress = Namespace + "/anonymous";

        /// <summary>A reference that is an address and nothing more.</summary>
        public EndpointReference(string address)
            : this(address, null)
        {
        }

        /// <param name="address">Where the messages go, sent back exactly as it is given.</param>
        /// <param name="referenceParameters">
        /// The elements the service put in the reference's ReferenceParameters, or null for none.
        /// Each goes back as a header of its own.
        /// </param>
        public EndpointReference(string address, IEnumerable<XmlElement> referenceParameters)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentNullException(nameof(address));

            var parameters = new List<XmlElement>();
            if (referenceParameters != null)
            {
                foreach (XmlElement parameter in referenceParameters)
                {
                    if (parameter != null) parameters.Add(parameter);
                }
            }

            Address = address;
            ReferenceParameters = parameters.AsReadOnly();
        }

        /// <summary>The address, as the service gave it.</summary>
        public string Address { get; private set; }

        /// <summary>The reference parameters, in the order the service gave them.</summary>
        public IList<XmlElement> ReferenceParameters { get; private set; }

        /// <summary>
        /// Writes the addressing headers of one message. Called inside the SOAP header, with the
        /// action the message is sent under.
        /// </summary>
        public void WriteHeaders(IXmlWriter writer, string action)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));

            XmlWriter xml = writer.Xml;

            if (!string.IsNullOrEmpty(action))
                xml.WriteElementString("wsa", "Action", Namespace, action);

            xml.WriteElementString("wsa", "MessageID", Namespace, "urn:uuid:" + Guid.NewGuid().ToString("D"));

            xml.WriteStartElement("wsa", "ReplyTo", Namespace);
            xml.WriteElementString("wsa", "Address", Namespace, AnonymousAddress);
            xml.WriteEndElement();

            xml.WriteElementString("wsa", "To", Namespace, Address);

            foreach (XmlElement parameter in ReferenceParameters)
            {
                // A copy, so that marking it leaves the reference as the service gave it.
                var header = (XmlElement)parameter.CloneNode(true);
                XmlAttribute marker = header.OwnerDocument.CreateAttribute("wsa", "IsReferenceParameter", Namespace);
                marker.Value = "true";
                header.SetAttributeNode(marker);
                header.WriteTo(xml);
            }
        }
    }
}
