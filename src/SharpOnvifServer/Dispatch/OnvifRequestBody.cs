using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace SharpOnvifServer.Dispatch
{
    /// <summary>
    /// The body of an Onvif request, read once.
    /// <para>
    /// Authentication needs it - for the action a PRE_AUTH request names in its header, for a
    /// WS-UsernameToken, for an auth-int digest - and so does the endpoint. Each used to read and
    /// copy it again, and parse it again; the bytes are kept here for the rest of the request,
    /// and the request's own body is consumed once they have been.
    /// </para>
    /// </summary>
    internal static class OnvifRequestBody
    {
        private static readonly object Key = new object();

        /// <summary>Recorded for a body over the limit, so a second reader refuses it without reading.</summary>
        private static readonly object TooLarge = new object();

        /// <summary>
        /// The request body. Null when the read was cancelled before the body was complete.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The body is larger than <see cref="OnvifEndpoint.MaxRequestBytes"/>.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// The client went away before all of it arrived.
        /// </exception>
        public static async Task<byte[]> ReadAsync(HttpContext context)
        {
            if (context.Items.TryGetValue(Key, out object kept))
            {
                if (kept == TooLarge) throw Refused();
                return (byte[])kept;
            }

            long maximum = OnvifEndpoint.MaxRequestBytes;

            long? declared = context.Request.ContentLength;
            if (declared.HasValue && declared.Value > maximum)
            {
                context.Items[Key] = TooLarge;
                throw Refused();
            }

            PipeReader reader = context.Request.BodyReader;
            while (true)
            {
                ReadResult read = await reader.ReadAsync(context.RequestAborted).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = read.Buffer;

                if (read.IsCompleted && buffer.Length <= maximum)
                {
                    byte[] body = buffer.ToArray();
                    context.Items[Key] = body;

                    // Consumed, now that it is kept: everything after this reads the copy. A body
                    // left in the pipe is one the server still has to drain when the request
                    // ends, and on a connection the client has closed by then it reported that as
                    // the connection ending abnormally - a warning for every pull a client
                    // abandoned.
                    reader.AdvanceTo(buffer.End);
                    return body;
                }

                // Consumed nothing, examined everything: the next read waits for more.
                reader.AdvanceTo(buffer.Start, buffer.End);

                if (read.IsCanceled) return null;

                // A chunked request declares no length, so the limit is enforced as it arrives.
                if (buffer.Length > maximum)
                {
                    context.Items[Key] = TooLarge;
                    throw Refused();
                }
            }
        }

        private static InvalidDataException Refused() =>
            new InvalidDataException("The request is larger than this endpoint accepts.");
    }
}
