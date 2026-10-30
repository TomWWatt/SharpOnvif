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
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpOnvifClient;
using SharpOnvifCommon;
using SharpOnvifServer.Dispatch;

namespace SharpOnvif.Tests
{
    /// <summary>
    /// SimpleOnvifClient against a device, which exercises the step the generated clients do not:
    /// discovering where a service lives.
    /// <para>
    /// The client reads the addresses GetServices advertises and sends every later call there. A
    /// device that advertises an address it does not serve leaves the client calling into
    /// nothing, and the failure surfaces far from its cause - so this asserts that what the
    /// device publishes is actually reachable.
    /// </para>
    /// </summary>
    [TestClass]
    public sealed class TestSimpleOnvifClientEndToEnd
    {
        private const string EndpointPath = "/onvif/device_service";

        private static WebApplication _app;
        private static string _endpoint;

        private sealed class DeviceImpl : SharpOnvifServer.DeviceMgmt.DeviceBase
        {
            public override SharpOnvifServer.DeviceMgmt.GetServicesResponse GetServices(
                SharpOnvifServer.DeviceMgmt.GetServicesRequest request)
            {
                // Advertise the address this request arrived on, which is what a device serving
                // several services from one endpoint does.
                string address = OnvifOperationContext.RequestUri.ToString();

                return new SharpOnvifServer.DeviceMgmt.GetServicesResponse
                {
                    Service = new[]
                    {
                        Service(OnvifServices.DEVICE_MGMT, address),
                        Service(OnvifServices.MEDIA, address),
                        Service(OnvifServices.MEDIA2, address),
                    },
                };
            }

            private static SharpOnvifServer.DeviceMgmt.Service Service(string ns, string address)
            {
                return new SharpOnvifServer.DeviceMgmt.Service
                {
                    Namespace = ns,
                    XAddr = address,
                    Version = new SharpOnvifCommon.Onvif.OnvifVersion { Major = 2, Minor = 6 },
                };
            }

            public override SharpOnvifServer.DeviceMgmt.GetDeviceInformationResponse GetDeviceInformation()
            {
                return new SharpOnvifServer.DeviceMgmt.GetDeviceInformationResponse { Manufacturer = "ACME" };
            }
        }

        private sealed class MediaImpl : SharpOnvifServer.Media.MediaBase
        {
            public override SharpOnvifServer.Media.GetProfilesResponse GetProfiles(
                SharpOnvifServer.Media.GetProfilesRequest request)
            {
                return new SharpOnvifServer.Media.GetProfilesResponse
                {
                    Profiles = new[] { new SharpOnvifCommon.Onvif.Profile { token = "p0", Name = "MainStream" } },
                };
            }

            public override SharpOnvifServer.Media.GetStreamUriResponse GetStreamUri(
                SharpOnvifCommon.Onvif.StreamSetup StreamSetup, string ProfileToken)
            {
                return new SharpOnvifServer.Media.GetStreamUriResponse
                {
                    MediaUri = new SharpOnvifCommon.Onvif.MediaUri
                    {
                        Uri = "rtsp://localhost:8554/" + ProfileToken,
                    },
                };
            }

            /// <summary>The profile the last synchronization point was asked for.</summary>
            public static string LastSynchronizationPointToken;

            public override SharpOnvifServer.Media.SetSynchronizationPointResponse SetSynchronizationPoint(string ProfileToken)
            {
                LastSynchronizationPointToken = ProfileToken;
                return new SharpOnvifServer.Media.SetSynchronizationPointResponse();
            }
        }

        /// <summary>The same device's Media 2 service, for the operations that exist on both.</summary>
        private sealed class Media2Impl : SharpOnvifServer.Media2.Media2Base
        {
            public static string LastSynchronizationPointToken;

            public override SharpOnvifServer.Media2.SetSynchronizationPointResponse SetSynchronizationPoint(string ProfileToken)
            {
                LastSynchronizationPointToken = ProfileToken;
                return new SharpOnvifServer.Media2.SetSynchronizationPointResponse();
            }
        }

        [ClassInitialize]
        public static async Task StartServer(TestContext context)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            builder.Services.AddSingleton<DeviceImpl>();
            builder.Services.AddSingleton<MediaImpl>();
            builder.Services.AddSingleton<Media2Impl>();

            _app = builder.Build();
            _app.MapOnvifService<DeviceImpl>(EndpointPath);
            _app.MapOnvifService<MediaImpl>(EndpointPath);
            _app.MapOnvifService<Media2Impl>(EndpointPath);

            await _app.StartAsync();

            string address = _app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>().Addresses.First();
            _endpoint = address.TrimEnd('/') + EndpointPath;
        }

        [ClassCleanup]
        public static async Task StopServer()
        {
            if (_app != null) await _app.StopAsync();
        }

        [TestMethod]
        public async Task ReachesAServiceThroughTheAddressTheDeviceAdvertises()
        {
            using (var client = new SimpleOnvifClient(_endpoint))
            {
                var services = await client.GetServicesAsync(true);
                Assert.IsNotNull(services.Service.FirstOrDefault(x => x.Namespace == OnvifServices.MEDIA),
                    "the device has to advertise the media service for the rest of this to mean anything");

                // Goes to whatever GetServices advertised, not to the endpoint we started from.
                var profiles = await client.GetProfilesAsync();

                Assert.AreEqual(1, profiles.Profiles.Length);
                Assert.AreEqual("MainStream", profiles.Profiles[0].Name);
            }
        }

        [TestMethod]
        public async Task FollowsTheAdvertisedAddressForEveryLaterCall()
        {
            using (var client = new SimpleOnvifClient(_endpoint))
            {
                var stream = await client.GetStreamUriAsync("p0");
                Assert.AreEqual("rtsp://localhost:8554/p0", stream.Uri);
            }
        }

        [TestMethod]
        public async Task ReportsAServiceTheDeviceDoesNotSupport()
        {
            using (var client = new SimpleOnvifClient(_endpoint))
            {
                // PTZ is not advertised, so resolving it has to fail with something that says so
                // rather than calling an address that does not exist.
                await Assert.ThrowsExactlyAsync<NotSupportedException>(
                    () => client.GetStatusAsync("p0"));
            }
        }

        [TestMethod]
        public async Task AsksForAKeyframeOnTheProfileItIsGiven()
        {
            // A viewer that has just joined a stream waits for the next keyframe before it can
            // show anything, and on a long group of pictures that is seconds. The device is asked
            // for one now, through whichever Media service the caller is working with.
            using (var client = new SimpleOnvifClient(_endpoint))
            {
                await client.SetSynchronizationPointAsync("p-media1");
                Assert.AreEqual("p-media1", MediaImpl.LastSynchronizationPointToken, "Media 1 was not asked, or not for this profile");

                await client.SetSynchronizationPoint2Async("p-media2");
                Assert.AreEqual("p-media2", Media2Impl.LastSynchronizationPointToken, "Media 2 was not asked, or not for this profile");
            }
        }
    }
}
