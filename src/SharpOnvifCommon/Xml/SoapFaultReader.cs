using System;
using System.Xml;

namespace SharpOnvifCommon.Xml
{
    /// <summary>
    /// Reads a <see cref="SoapFault"/> off the wire. The fault itself is part of what a client
    /// hands its caller and so is generated; picking one out of a reply is this library's job.
    /// </summary>
    public static class SoapFaultReader
    {
        /// <summary>Reads a fault from a reader positioned on the SOAP Fault element.</summary>
        public static SoapFault Read(XmlReader reader)
        {
            var fault = new SoapFault();
            int faultDepth = reader.Depth;

            if (reader.IsEmptyElement)
            {
                reader.Read();
                return fault;
            }

            // Step past the Fault start tag. Every branch below leaves the reader on the node
            // after what it read, so nothing here advances it twice: reading an element's content
            // already moves past its end tag, and reading again from there stepped over whatever
            // came next - which in a fault written without whitespace is the next part of the
            // fault. The Reason of a fault with no subcode was consumed inside its Code that way,
            // and the exception said "Sender" and nothing else.
            reader.Read();

            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == faultDepth) { reader.Read(); break; }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "Code":
                        ReadCode(reader, fault);
                        break;
                    case "Reason":
                        fault.Reason = ReadReason(reader);
                        break;
                    case "Detail":
                        fault.Detail = reader.ReadInnerXml();
                        break;
                    default:
                        // Node, Role, or something of the device's own.
                        reader.Skip();
                        break;
                }
            }

            return fault;
        }

        /// <summary>
        /// Reads Code/Value plus the chain of nested Subcode/Value elements, and leaves the reader
        /// after the Code.
        /// </summary>
        private static void ReadCode(XmlReader reader, SoapFault fault)
        {
            int codeDepth = reader.Depth;
            if (reader.IsEmptyElement)
            {
                reader.Read();
                return;
            }

            bool first = true;
            reader.Read();

            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == codeDepth)
                {
                    reader.Read();
                    return;
                }

                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Value")
                {
                    string value = Localise(reader.ReadElementContentAsString());
                    if (first) { fault.Code = value; first = false; }
                    else fault.Subcodes.Add(value);
                    continue;
                }

                reader.Read();
            }
        }

        /// <summary>
        /// Takes the first Text child of Reason - Onvif devices send a single language - and
        /// leaves the reader after the Reason.
        /// </summary>
        private static string ReadReason(XmlReader reader)
        {
            int reasonDepth = reader.Depth;
            if (reader.IsEmptyElement)
            {
                reader.Read();
                return null;
            }

            string text = null;
            reader.Read();

            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == reasonDepth)
                {
                    reader.Read();
                    return text;
                }

                if (text == null && reader.NodeType == XmlNodeType.Element && reader.LocalName == "Text")
                {
                    text = reader.ReadElementContentAsString();
                    continue;
                }

                reader.Read();
            }

            return text;
        }

        /// <summary>
        /// Drops the prefix from a fault code QName. The prefix binding is rarely useful and the
        /// local part is what callers match on.
        /// </summary>
        private static string Localise(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            int colon = value.IndexOf(':');
            return colon < 0 ? value : value.Substring(colon + 1);
        }
    }
}
